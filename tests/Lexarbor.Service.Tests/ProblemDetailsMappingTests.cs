using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Exception-generated failure responses after the ServiceMantle Problem Details
/// migration: a fixed type/title/status/errorCode plus a correlation id, the
/// caller-retryable 503 with its bounded Retry-After, the safe 500 for unknown
/// failures, the Kestrel 413 projection, the response-already-started boundary,
/// and the coexistence with the endpoint-explicit envelope, which is unchanged.
/// </summary>
public class ProblemDetailsMappingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/admin/vocabulary/missing-word", """{"word":"x","phoneticUk":null,"phoneticUs":null}""", 404, "vocabulary.not_found")]
    [InlineData("/admin/vocabulary/missing-word", """{"word":" ","phoneticUk":null,"phoneticUs":null}""", 400, "vocabulary.validation")]
    public async Task ExceptionGeneratedFailures_CarryProblemDetailsContract(
        string path,
        string body,
        int expectedStatus,
        string expectedErrorCode)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken("admin"));

        using var response = await client.PutAsync(
            path,
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            Ct);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;
        Assert.Equal(expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedErrorCode, root.GetProperty("errorCode").GetString());
        Assert.Equal(
            "urn:servicemantle:error:" + expectedErrorCode,
            root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        var correlationId = root.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.Equal(correlationId, response.Headers.GetValues("x-correlation-id").Single());
    }

    [Fact]
    public async Task ConflictAndBusinessRule_KeepTheirStatusesAsProblemDetails()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken("admin"));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var bookId = await CreateBookAsync(factory, $"Conflict {suffix}", status: true);
        using (var imported = await client.PostAsJsonAsync(
            "/admin/vocabulary/batch",
            new
            {
                bookId,
                entries = new[]
                {
                    new { word = $"alpha{suffix}", meaning = "one" },
                    new { word = $"beta{suffix}", meaning = "two" }
                }
            },
            Ct))
        {
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        }

        // Renaming one word onto another's normalized value conflicts through the
        // domain service; the route carries the row id, not the word text.
        var alphaId = await FindWordIdAsync(factory, $"alpha{suffix}");
        using var conflict = await client.PutAsync(
            $"/admin/vocabulary/{alphaId}",
            new StringContent(
                $$"""{"word":"beta{{suffix}}","phoneticUk":null,"phoneticUs":null}""",
                System.Text.Encoding.UTF8,
                "application/json"),
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync(Ct));
        Assert.Equal("vocabulary.conflict", conflictBody.RootElement.GetProperty("errorCode").GetString());

        // Adding meanings to a disabled book violates a business rule.
        var disabledBookId = await CreateBookAsync(factory, $"Disabled {suffix}", status: false);
        using var rule = await client.PostAsJsonAsync(
            "/admin/vocabulary/batch",
            new { bookId = disabledBookId, entries = new[] { new { word = "word", meaning = "meaning" } } },
            Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rule.StatusCode);
        using var ruleBody = JsonDocument.Parse(await rule.Content.ReadAsStringAsync(Ct));
        Assert.Equal("vocabulary.business_rule", ruleBody.RootElement.GetProperty("errorCode").GetString());
    }

    private static async Task<string> FindWordIdAsync(VocabularyWebApplicationFactory factory, string word)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<Lexarbor.Database.VocabularyDbContext>();
        var row = await context.Vocabularies.SingleAsync(v => v.Word == word, Ct);
        return row.Id;
    }

    private static async Task<string> CreateBookAsync(
        VocabularyWebApplicationFactory factory,
        string name,
        bool status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<Lexarbor.Database.VocabularyDbContext>();
        var bookId = $"problem-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        context.VocabularyBooks.Add(new Lexarbor.Database.Entities.VocabularyBookEntity
        {
            Id = bookId,
            BookName = name,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync(Ct);
        return bookId;
    }

    [Fact]
    public async Task StorageBusy_Answers503WithBoundedRetryAfter()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVocabularyRepository>();
            services.AddScoped<IVocabularyRepository>(_ => new ThrowingRepository(
                () => throw new Lexarbor.Domain.Exceptions.StorageBusyException("busy details must not leak")));
        }));
        using var client = configured.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary?keyword=x&page=1&size=20", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("busy details must not leak", raw);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal("vocabulary.storage_busy", body.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task UnknownException_AnswersSafe500WithoutInternalDetails()
    {
        const string secret = "postgresql://internal-user:internal-password@database/vocabulary";
        await using var factory = new VocabularyWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVocabularyRepository>();
            services.AddScoped<IVocabularyRepository>(_ => new ThrowingRepository(
                () => throw new InvalidOperationException(secret)));
        }));
        using var client = configured.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary?keyword=x&page=1&size=20", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain(nameof(InvalidOperationException), raw);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal("http.internal_server_error", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("An unexpected error occurred.", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ExceptionAfterResponseStarted_LeavesSentResponseUnchanged()
    {
        // The injected middleware is outermost, so by the time the real pipeline
        // throws, the response has already started and the Problem Details
        // middleware must leave the already sent bytes alone.
        await using var factory = new VocabularyWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVocabularyRepository>();
                services.AddScoped<IVocabularyRepository>(_ => new ThrowingRepository(
                    () => throw new Lexarbor.Domain.Exceptions.ConflictException("late conflict")));
                services.AddSingleton<IStartupFilter>(new PartialResponseStartupFilter());
            });
        });
        using var client = configured.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary?keyword=x&page=1&size=20", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("partial", raw);
        Assert.DoesNotContain("problem", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EndpointExplicitFailures_KeepTheLegacyEnvelope()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken("admin"));

        using var response = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = " ", status = true },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task ClientCancellation_DoesNotProduceAProblemResponse()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new VocabularyWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVocabularyRepository>();
            services.AddScoped<IVocabularyRepository>(__ => new GatedRepository(gate));
        }));
        using var client = configured.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var sending = client.GetAsync("/api/vocabulary?keyword=x&page=1&size=20", cancellation.Token);
        cancellation.Cancel();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
    }

    private sealed class PartialResponseStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, nextMiddleware) =>
            {
                await context.Response.WriteAsync("partial");
                await context.Response.Body.FlushAsync();
                await nextMiddleware();
            });
            next(builder);
        };
    }

    private sealed class ThrowingRepository(Func<Exception> exceptionFactory) : IVocabularyRepository
    {
        public Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(
            string? keyword, int page, int size) => throw exceptionFactory();
        public Task<VocabularyModel?> GetByIdAsync(string id) => throw Unused();
        public Task<VocabularyModel?> GetByWordAsync(string word) => throw Unused();
        public Task<VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord) => throw Unused();
        public Task<List<VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids) => throw Unused();
        public Task AddAsync(VocabularyModel model) => throw Unused();
        public Task UpdateAsync(VocabularyModel model) => throw Unused();
        public Task<List<VocabularyModel>> GetRandomByBookExceptAsync(
            string bookId, string excludeVocabularyId, string excludeWord,
            string excludeEquivalentMeaning, int count,
            VocabularyDistractorScope? scope = null) => throw Unused();

        private static NotSupportedException Unused() => new();
    }

    private sealed class GatedRepository(TaskCompletionSource gate) : IVocabularyRepository
    {
        public async Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(
            string? keyword, int page, int size)
        {
            await gate.Task;
            throw new InvalidOperationException("unreachable");
        }

        public Task<VocabularyModel?> GetByIdAsync(string id) => throw Unused();
        public Task<VocabularyModel?> GetByWordAsync(string word) => throw Unused();
        public Task<VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord) => throw Unused();
        public Task<List<VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids) => throw Unused();
        public Task AddAsync(VocabularyModel model) => throw Unused();
        public Task UpdateAsync(VocabularyModel model) => throw Unused();
        public Task<List<VocabularyModel>> GetRandomByBookExceptAsync(
            string bookId, string excludeVocabularyId, string excludeWord,
            string excludeEquivalentMeaning, int count,
            VocabularyDistractorScope? scope = null) => throw Unused();

        private static NotSupportedException Unused() => new();
    }
}
