using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

/// <summary>
/// <c>POST /admin/vocabulary/batch</c> against the semantic model in ADR-005.
/// Row and scenario numbers refer to that model as written in issue #72. Every
/// rejection also checks that the database is untouched.
/// </summary>
public class VocabularyBatchImportEndpointTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private const string BatchPath = "/admin/vocabulary/batch";

    private readonly VocabularyWebApplicationFactory _factory;

    public VocabularyBatchImportEndpointTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Row 1.
    [Fact]
    public async Task Unauthenticated_Returns401AndWritesNothing()
    {
        var bookId = await CreateBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(bookId, Entry("apple", "苹果")),
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
        await AssertBookIsEmptyAsync(bookId);
    }

    // Row 2.
    [Fact]
    public async Task AuthenticatedNonAdmin_Returns403AndWritesNothing()
    {
        var bookId = await CreateBookAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("student"));

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(bookId, Entry("apple", "苹果")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertBookIsEmptyAsync(bookId);
    }

    // Row 4.
    [Theory]
    [InlineData("{ \"bookId\": ", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{ \"bookId\": \"b\", \"entries\": \"not-a-list\" }", "application/json")]
    [InlineData("{ \"bookId\": \"b\", \"entries\": [] }", "text/plain")]
    public async Task BodyThatIsNotValidJson_Returns400(string body, string contentType)
    {
        using var client = CreateAdminClient();
        using var content = new StringContent(body, Encoding.UTF8, contentType);

        var response = await client.PostAsync(BatchPath, content, TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("The request body is not valid JSON.", envelope.GetProperty("message").GetString());
    }

    // Rows 5, 6 and 7 (scenario 5), in the order the model checks them: a
    // request that fails several reports only the first.
    [Theory]
    [InlineData(null, 1, "Book ID is required.")]
    [InlineData("  ", 1, "Book ID is required.")]
    [InlineData("  ", 0, "Book ID is required.")]
    [InlineData("BOOK", 0, "At least one entry is required.")]
    [InlineData("BOOK", 501, "A batch can contain at most 500 entries.")]
    public async Task InvalidBatchShape_Returns400WithTheFirstFailedCheck(
        string? bookId,
        int entryCount,
        string expectedMessage)
    {
        var realBookId = await CreateBookAsync();
        using var client = CreateAdminClient();
        var entries = Enumerable.Range(0, entryCount)
            .Select(index => Entry($"shape{index}-{realBookId}", "meaning"))
            .ToArray();

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(bookId == "BOOK" ? realBookId : bookId, entries),
            TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(expectedMessage, envelope.GetProperty("message").GetString());
        Assert.False(envelope.TryGetProperty("errors", out _));
        await AssertBookIsEmptyAsync(realBookId);
    }

    [Fact]
    public async Task MissingEntries_Returns400()
    {
        var bookId = await CreateBookAsync();
        using var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            BatchPath,
            new { bookId },
            TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("At least one entry is required.", envelope.GetProperty("message").GetString());
    }

    // Row 8, scenario 4: every invalid entry is reported, in index order, and
    // the valid ones around them are not written.
    [Fact]
    public async Task InvalidEntries_Returns400ListingEveryInvalidIndex()
    {
        var bookId = await CreateBookAsync();
        using var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(
                bookId,
                Entry("apple", "苹果"),
                Entry("banana", null),
                Entry("cherry", "樱桃"),
                Entry("   ", "枣"),
                Entry("elderberry", "接骨木果")),
            TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("2 entries are invalid.", envelope.GetProperty("message").GetString());
        var errors = envelope.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal([1, 3], errors.Select(error => error.GetProperty("index").GetInt32()));
        Assert.Equal(
            ["Meaning is required.", "Word is required."],
            errors.Select(error => error.GetProperty("message").GetString()));
        await AssertBookIsEmptyAsync(bookId);
    }

    [Fact]
    public async Task NullEntry_IsReportedAsInvalid()
    {
        var bookId = await CreateBookAsync();
        using var client = CreateAdminClient();
        using var content = new StringContent(
            $$"""{ "bookId": "{{bookId}}", "entries": [ null, { "word": "", "meaning": "" } ] }""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(BatchPath, content, TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("2 entries are invalid.", envelope.GetProperty("message").GetString());
        Assert.Equal(
            ["Entry is required.", "Word and meaning are required."],
            envelope.GetProperty("errors").EnumerateArray()
                .Select(error => error.GetProperty("message").GetString()));
        await AssertBookIsEmptyAsync(bookId);
    }

    [Fact]
    public async Task SingleInvalidEntry_UsesTheSingularMessage()
    {
        var bookId = await CreateBookAsync();
        using var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(bookId, Entry("apple", "苹果"), Entry("banana", " ")),
            TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("1 entry is invalid.", envelope.GetProperty("message").GetString());
        var error = Assert.Single(envelope.GetProperty("errors").EnumerateArray());
        Assert.Equal(1, error.GetProperty("index").GetInt32());
        await AssertBookIsEmptyAsync(bookId);
    }

    // Row 9.
    [Fact]
    public async Task UnknownBook_Returns404()
    {
        using var client = CreateAdminClient();
        var bookId = $"missing-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(bookId, Entry($"word-{bookId}", "meaning")),
            TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Vocabulary book was not found.", envelope.GetProperty("message").GetString());
        await AssertWordAbsentAsync($"word-{bookId}");
    }

    // Row 10, scenario 6.
    [Fact]
    public async Task DisabledBook_Returns422()
    {
        var bookId = await CreateBookAsync(status: false);
        using var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(bookId, Entry($"word-{bookId}", "meaning")),
            TestContext.Current.CancellationToken);

        var envelope = await AssertFailureAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(
            "New meanings cannot be added to a disabled vocabulary book.",
            envelope.GetProperty("message").GetString());
        await AssertBookIsEmptyAsync(bookId);
        await AssertWordAbsentAsync($"word-{bookId}");
    }

    // Row 11, scenarios 1-3. The response data carries exactly three fields.
    [Fact]
    public async Task ValidBatch_ImportsAndReportsCounts()
    {
        var bookId = await CreateBookAsync();
        var suffix = bookId[^8..];
        using var client = CreateAdminClient();
        var batch = Batch(
            bookId,
            Entry($"apple{suffix}", "苹果", phoneticUk: "/ˈæp.əl/", partOfSpeech: "n.", example: "I eat an apple."),
            Entry($"banana{suffix}", "香蕉"),
            Entry($"cherry{suffix}", "樱桃"));

        var first = await PostForDataAsync(client, batch);
        var second = await PostForDataAsync(client, batch);
        var third = await PostForDataAsync(
            client,
            Batch(
                bookId,
                Entry($"Durian{suffix}", "榴莲", partOfSpeech: "n."),
                Entry($" durian{suffix} ", "榴莲", partOfSpeech: "N."),
                Entry($"DURIAN{suffix}", " 榴莲 ", partOfSpeech: " n. ")));

        Assert.Equal(["created", "reused", "total"], first.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((3, 3, 0), Counts(first));
        Assert.Equal((3, 0, 3), Counts(second));
        Assert.Equal((3, 1, 2), Counts(third));

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(
            4,
            await context.VocabularyMeanings.CountAsync(
                meaning => meaning.BookId == bookId,
                TestContext.Current.CancellationToken));
        var apple = await context.Vocabularies.SingleAsync(
            word => word.Word == $"apple{suffix}",
            TestContext.Current.CancellationToken);
        Assert.Equal("/ˈæp.əl/", apple.PhoneticUk);
    }

    // Row 12, scenario 7: a failure while writing the third entry rolls back the
    // two before it, and each failure keeps its existing status mapping.
    [Theory]
    [InlineData("conflict", HttpStatusCode.Conflict)]
    [InlineData("busy", HttpStatusCode.ServiceUnavailable)]
    [InlineData("unexpected", HttpStatusCode.InternalServerError)]
    public async Task FailureWhileWriting_RollsBackTheWholeBatch(string failure, HttpStatusCode expected)
    {
        var bookId = await CreateBookAsync();
        var suffix = bookId[^8..];
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVocabularyMeaningRepository>();
                services.AddScoped<IVocabularyMeaningRepository>(provider =>
                    new FailingOnThirdAddMeaningRepository(
                        new Lexarbor.Database.Repositories.VocabularyMeaningRepository(
                            provider.GetRequiredService<VocabularyDbContext>()),
                        failure switch
                        {
                            "conflict" => new ConflictException("The requested vocabulary data conflicts with existing data."),
                            "busy" => new StorageBusyException("The vocabulary database is busy. Please retry the request."),
                            _ => new InvalidOperationException("boom")
                        }));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("admin"));

        var response = await client.PostAsJsonAsync(
            BatchPath,
            Batch(
                bookId,
                Entry($"apple{suffix}", "苹果"),
                Entry($"banana{suffix}", "香蕉"),
                Entry($"cherry{suffix}", "樱桃")),
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, expected);
        if (expected == HttpStatusCode.ServiceUnavailable)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        }

        await AssertBookIsEmptyAsync(bookId);
        await AssertWordAbsentAsync($"apple{suffix}");
        await AssertWordAbsentAsync($"banana{suffix}");
    }

    // Row 3, scenario 9. TestServer does not enforce request size limits, so
    // this runs on its own Kestrel-hosted factory rather than the shared one.
    [Fact]
    public async Task BodyOverOneMebibyte_Returns413OnKestrel()
    {
        using var factory = new VocabularyWebApplicationFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var bookId = await CreateBookAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));

        var atLimit = PaddedBatch(bookId, VocabularyHttpEndpoints.MaxBatchRequestBytes);
        var overLimit = PaddedBatch(bookId, VocabularyHttpEndpoints.MaxBatchRequestBytes + 1);

        using (var content = new ByteArrayContent(overLimit))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await client.PostAsync(BatchPath, content, TestContext.Current.CancellationToken);
            var envelope = await AssertFailureAsync(response, HttpStatusCode.RequestEntityTooLarge);
            Assert.Equal("The request body is too large.", envelope.GetProperty("message").GetString());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, BatchPath))
        {
            // No Content-Length, so the limit has to be enforced while reading.
            request.Content = new StreamContent(new NonSeekableStream(overLimit));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TransferEncodingChunked = true;
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var envelope = await AssertFailureAsync(response, HttpStatusCode.RequestEntityTooLarge);
            Assert.Equal("The request body is too large.", envelope.GetProperty("message").GetString());
        }

        await AssertBookIsEmptyAsync(bookId, factory);

        using (var content = new ByteArrayContent(atLimit))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await client.PostAsync(BatchPath, content, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("admin"));
        return client;
    }

    private static object Batch(string? bookId, params object[] entries) => new { bookId, entries };

    private static object Entry(
        string word,
        string? meaning,
        string? phoneticUk = null,
        string? partOfSpeech = null,
        string? example = null) =>
        new { word, meaning, phoneticUk, partOfSpeech, example };

    /// <summary>
    /// A valid one-entry batch padded with trailing whitespace, which JSON
    /// allows, to exactly <paramref name="length"/> bytes.
    /// </summary>
    private static byte[] PaddedBatch(string bookId, long length)
    {
        var json = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(Batch(bookId, Entry($"padded-{bookId}", "meaning"))));
        var body = new byte[length];
        Array.Fill(body, (byte)' ');
        json.CopyTo(body, 0);
        return body;
    }

    private static async Task<JsonElement> PostForDataAsync(HttpClient client, object batch)
    {
        var response = await client.PostAsJsonAsync(BatchPath, batch, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        return body.RootElement.GetProperty("data").Clone();
    }

    private static (int Total, int Created, int Reused) Counts(JsonElement data) => (
        data.GetProperty("total").GetInt32(),
        data.GetProperty("created").GetInt32(),
        data.GetProperty("reused").GetInt32());

    private Task<string> CreateBookAsync(bool status = true) => CreateBookAsync(_factory, status);

    private static async Task<string> CreateBookAsync(VocabularyWebApplicationFactory factory, bool status = true)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var bookId = $"batch-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        context.VocabularyBooks.Add(new VocabularyBookEntity
        {
            Id = bookId,
            BookName = $"Batch {bookId}",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return bookId;
    }

    private Task AssertBookIsEmptyAsync(string bookId) => AssertBookIsEmptyAsync(bookId, _factory);

    private static async Task AssertBookIsEmptyAsync(string bookId, VocabularyWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(
            0,
            await context.VocabularyMeanings.CountAsync(
                meaning => meaning.BookId == bookId,
                TestContext.Current.CancellationToken));
    }

    private async Task AssertWordAbsentAsync(string word)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.False(await context.Vocabularies.AnyAsync(
            vocabulary => vocabulary.Word == word.Trim().ToLowerInvariant(),
            TestContext.Current.CancellationToken));
    }

    private static async Task<JsonElement> AssertFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("message").GetString()));
        return body.RootElement.Clone();
    }

    /// <summary>
    /// Adds normally until the third meaning, then fails the way the storage
    /// layer or an unexpected fault would.
    /// </summary>
    private sealed class FailingOnThirdAddMeaningRepository(
        IVocabularyMeaningRepository inner,
        Exception failure) : IVocabularyMeaningRepository
    {
        private int _addCount;

        public Task AddAsync(VocabularyMeaningModel model) =>
            ++_addCount == 3 ? throw failure : inner.AddAsync(model);

        public Task<VocabularyMeaningModel?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task<List<VocabularyMeaningModel>> GetByVocabularyIdAsync(string vocabularyId) =>
            inner.GetByVocabularyIdAsync(vocabularyId);
        public Task<List<VocabularyMeaningModel>> GetByBookIdAsync(string bookId) => inner.GetByBookIdAsync(bookId);
        public Task<List<VocabularyMeaningModel>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId) =>
            inner.GetByBookAndVocabularyIdAsync(bookId, vocabularyId);
        public Task<VocabularyMeaningModel?> GetEquivalentAsync(
            string vocabularyId,
            string bookId,
            string normalizedPartOfSpeech,
            string meaning) => inner.GetEquivalentAsync(vocabularyId, bookId, normalizedPartOfSpeech, meaning);
        public Task<List<VocabularyMeaningModel>> GetRandomDistinctVocabularyExceptAsync(
            string bookId,
            string excludeVocabularyId,
            string excludeMeaning,
            int count) =>
            inner.GetRandomDistinctVocabularyExceptAsync(bookId, excludeVocabularyId, excludeMeaning, count);
        public Task UpdateAsync(VocabularyMeaningModel model) => inner.UpdateAsync(model);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task DeleteByVocabularyIdAsync(string vocabularyId) => inner.DeleteByVocabularyIdAsync(vocabularyId);
    }

    /// <summary>Hides the length so the client has to send the body chunked.</summary>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
