using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public class VocabularyHttpEndpointTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private readonly VocabularyWebApplicationFactory _factory;

    public VocabularyHttpEndpointTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/vocabulary?keyword=x&page=-1&size=20")]
    [InlineData("/api/vocabulary?keyword=x&page=1&size=101")]
    public async Task VocabularySearch_InvalidPaging_Returns400Envelope(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdminWrite_MalformedJson_Returns400Envelope()
    {
        using var client = CreateAdminClient();
        using var content = new StringContent(
            "{ \"bookName\": ",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/admin/vocabulary-books", content,
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateUnknownBook_Returns404Envelope()
    {
        using var client = CreateAdminClient();

        // A complete body, so the 404 comes from the missing target rather than
        // from the replace path's required-field checks.
        var response = await client.PutAsJsonAsync(
            "/admin/vocabulary-books",
            new
            {
                id = $"missing-{Guid.NewGuid():N}",
                bookName = "Missing Book",
                displayOrder = 0,
                status = true
            }, TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.NotFound);
    }

    // PUT replaces the stored book, so a field the request omits is written back
    // as its default. These three cases used to answer 200 and quietly blank the
    // name, reorder the book, or disable it -- which also removes it from the
    // public catalogue and makes every word in it answer 422.
    [Fact]
    public async Task UpdateBook_MissingBookName_Returns400AndLeavesBookIntact()
    {
        var (bookId, _) = await SeedBookAsync(distractorCount: 0);
        using var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/admin/vocabulary-books",
            new { id = bookId, displayOrder = 7, status = false },
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        var book = await GetBookAsync(client, bookId);
        Assert.False(string.IsNullOrWhiteSpace(book.GetProperty("bookName").GetString()));
        Assert.True(book.GetProperty("status").GetBoolean());
        Assert.Equal(0, book.GetProperty("displayOrder").GetInt32());
    }

    [Fact]
    public async Task UpdateBook_MissingStatus_Returns400AndLeavesBookEnabled()
    {
        var (bookId, _) = await SeedBookAsync(distractorCount: 0);
        using var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/admin/vocabulary-books",
            new { id = bookId, bookName = "Renamed", displayOrder = 0 },
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.True((await GetBookAsync(client, bookId)).GetProperty("status").GetBoolean());
    }

    [Fact]
    public async Task UpdateBook_MissingDisplayOrder_Returns400Envelope()
    {
        var (bookId, _) = await SeedBookAsync(distractorCount: 0);
        using var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/admin/vocabulary-books",
            new { id = bookId, bookName = "Renamed", status = true },
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateBook_WithCompleteBody_ReplacesEveryField()
    {
        var (bookId, _) = await SeedBookAsync(distractorCount: 0);
        using var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/admin/vocabulary-books",
            new
            {
                id = bookId,
                bookName = "Renamed",
                description = "A description",
                displayOrder = 7,
                status = false
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var book = await GetBookAsync(client, bookId);
        Assert.Equal("Renamed", book.GetProperty("bookName").GetString());
        Assert.Equal("A description", book.GetProperty("description").GetString());
        Assert.Equal(7, book.GetProperty("displayOrder").GetInt32());
        Assert.False(book.GetProperty("status").GetBoolean());
    }

    [Fact]
    public async Task CreateBook_WithNonEmptyId_Returns400Envelope()
    {
        using var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new
            {
                id = "must-not-update-through-create",
                bookName = "Create only",
                status = true
            }, TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteUsedBook_Returns409Envelope()
    {
        var (bookId, _) = await SeedBookAsync(distractorCount: 0);
        using var client = CreateAdminClient();

        var response = await client.DeleteAsync($"/admin/vocabulary-books/{bookId}",
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task QuestionWithTooFewCandidates_Returns422Envelope()
    {
        var (bookId, wordId) = await SeedBookAsync(distractorCount: 1);
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new { wordId, bookId, chineseToEnglish = true }, TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task RepositoryFailure_ReturnsGeneric500Envelope()
    {
        const string secret = "postgresql://internal-user:internal-password@database/vocabulary";
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVocabularyRepository>();
                services.AddScoped<IVocabularyRepository>(
                    _ => new ThrowingVocabularyRepository(secret));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/vocabulary?keyword=test&page=1&size=20", TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownApiPath_Returns404Envelope()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/not-a-real-endpoint",
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownAdminPath_WithoutAuth_Returns401Envelope()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/not-a-real-endpoint",
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownAdminPath_WithAdminToken_Returns404Envelope()
    {
        using var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/not-a-real-endpoint",
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_IsAnonymousAndUsesEnvelope()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("healthy", data.GetProperty("status").GetString());
        // The endpoint is anonymous, so anything it returns is public. Asserting
        // status is the only property keeps that surface from growing back by
        // accident: a field added here fails this test rather than shipping.
        Assert.Equal(
            ["status"],
            data.EnumerateObject().Select(property => property.Name).ToArray());
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("admin"));
        return client;
    }

    private static async Task<JsonElement> GetBookAsync(HttpClient client, string bookId)
    {
        var response = await client.GetAsync(
            $"/admin/vocabulary-books/{bookId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Cloned because the JsonDocument backing it is disposed on return.
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("data").Clone();
    }

    private async Task<(string BookId, string CorrectWordId)> SeedBookAsync(
        int distractorCount)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var bookId = $"book-{suffix}";
        var now = DateTimeOffset.UtcNow;
        context.VocabularyBooks.Add(new VocabularyBookEntity
        {
            Id = bookId,
            BookName = $"Book {suffix}",
            Status = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        var correctWordId = $"word-correct-{suffix}";
        AddWord(context, bookId, correctWordId, $"correct-{suffix}", "correct meaning", now);
        for (var index = 0; index < distractorCount; index++)
        {
            AddWord(
                context,
                bookId,
                $"word-{index}-{suffix}",
                $"word-{index}-{suffix}",
                $"meaning-{index}-{suffix}",
                now);
        }

        await context.SaveChangesAsync();
        return (bookId, correctWordId);
    }

    private static void AddWord(
        VocabularyDbContext context,
        string bookId,
        string wordId,
        string word,
        string meaning,
        DateTimeOffset now)
    {
        context.Vocabularies.Add(new VocabularyEntity
        {
            Id = wordId,
            Word = word,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.VocabularyMeanings.Add(new VocabularyMeaningEntity
        {
            Id = $"meaning-{wordId}",
            VocabularyId = wordId,
            BookId = bookId,
            Meaning = meaning,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static async Task AssertFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("message").GetString()));
    }

    private sealed class ThrowingVocabularyRepository : IVocabularyRepository
    {
        private readonly string _message;

        public ThrowingVocabularyRepository(string message)
        {
            _message = message;
        }

        public Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(
            string? keyword,
            int page,
            int size)
        {
            throw new InvalidOperationException(_message);
        }

        public Task<VocabularyModel?> GetByIdAsync(string id) => throw Unused();
        public Task<VocabularyModel?> GetByWordAsync(string word) => throw Unused();
        public Task<VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord) => throw Unused();
        public Task<List<VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids) => throw Unused();
        public Task AddAsync(VocabularyModel model) => throw Unused();
        public Task UpdateAsync(VocabularyModel model) => throw Unused();
        public Task<List<VocabularyModel>> GetRandomByBookExceptAsync(
            string bookId,
            string excludeVocabularyId,
            string excludeWord,
            string excludeEquivalentMeaning,
            int count) => throw Unused();

        private static NotSupportedException Unused() => new();
    }
}
