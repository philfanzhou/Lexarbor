using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

/// <summary>
/// <c>POST /api/vocabulary/questions</c>: several single-question requests in
/// one round trip. Every item is normalized and answered exactly as the single
/// endpoint answers the same request, one item's failure never fails the batch,
/// the results keep the request order, the batch body obeys the shared
/// ceilings, and a cancelled request stops before the next item.
/// </summary>
public class VocabularyQuestionBatchEndpointTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private const string BatchPath = "/api/vocabulary/questions";
    private const string SinglePath = "/api/vocabulary/question";

    private readonly VocabularyWebApplicationFactory _factory;

    public VocabularyQuestionBatchEndpointTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MixedItems_AnswerPerItemInRequestOrder()
    {
        var data = await SeedAsync(_factory);
        using var client = _factory.CreateClient();
        var missingMeaningId = $"bq-missing-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            BatchPath,
            new
            {
                items = new object?[]
                {
                    // A scoped, named item that must come back answered.
                    new
                    {
                        wordId = data.WordId,
                        bookId = data.BookId,
                        chineseToEnglish = true,
                        meaningId = data.MeaningId
                    },
                    // The item-level 400 the single endpoint answers itself.
                    new { wordId = " ", bookId = data.BookId },
                    // A meaning of no word in this book: the single 404.
                    new
                    {
                        wordId = data.WordId,
                        bookId = data.BookId,
                        chineseToEnglish = true,
                        meaningId = missingMeaningId
                    },
                    // The request shape a pre-scope caller sends.
                    new { wordId = data.WordId, bookId = data.BookId },
                    // A disabled book: the single 422.
                    new { wordId = data.WordId, bookId = data.DisabledBookId }
                }
            },
            TestContext.Current.CancellationToken);

        await AssertOkAsync(response);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var results = body.RootElement.GetProperty("data").GetProperty("results");

        // One row per item, in request order, whatever each item answered.
        Assert.Equal(5, results.GetArrayLength());
        var index = 0;
        foreach (var row in results.EnumerateArray())
        {
            Assert.Equal(index, row.GetProperty("index").GetInt32());
            index++;
        }

        var first = results[0];
        Assert.Equal(JsonValueKind.Null, first.GetProperty("error").ValueKind);
        var firstQuestion = first.GetProperty("question");
        Assert.Equal(data.WordId, firstQuestion.GetProperty("wordId").GetString());
        Assert.Equal(data.MeaningId, firstQuestion.GetProperty("meaningId").GetString());
        Assert.True(firstQuestion.GetProperty("chineseToEnglish").GetBoolean());
        Assert.Equal(4, firstQuestion.GetProperty("options").GetArrayLength());
        Assert.Single(
            firstQuestion.GetProperty("options").EnumerateArray(),
            option => option.GetProperty("isCorrect").GetBoolean());

        // The blank-required-fields item reports the message the single
        // endpoint's own 400 envelope carries.
        var second = results[1];
        AssertNullQuestionWithError(second, 400);
        var single400 = await client.PostAsJsonAsync(
            SinglePath,
            new { wordId = " ", bookId = data.BookId },
            TestContext.Current.CancellationToken);
        var envelope400 = await HttpFailureAssertions.AssertFailureAsync(
            single400, HttpStatusCode.BadRequest);
        Assert.Equal(
            envelope400.GetProperty("message").GetString(),
            second.GetProperty("error").GetProperty("message").GetString());

        // A failing item carries the same status and message the single
        // endpoint answers the very same request with, so a caller can retry
        // an item exactly as it would retry a single request.
        var third = results[2];
        AssertNullQuestionWithError(third, 404);
        var single404 = await client.PostAsJsonAsync(
            SinglePath,
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = true,
                meaningId = missingMeaningId
            },
            TestContext.Current.CancellationToken);
        var problem404 = await HttpFailureAssertions.AssertFailureAsync(
            single404, HttpStatusCode.NotFound);
        Assert.Equal(
            problem404.GetProperty("title").GetString(),
            third.GetProperty("error").GetProperty("message").GetString());

        Assert.Equal(JsonValueKind.Null, results[3].GetProperty("error").ValueKind);
        var fourthQuestion = results[3].GetProperty("question");
        Assert.Equal(data.WordId, fourthQuestion.GetProperty("wordId").GetString());
        Assert.Equal(4, fourthQuestion.GetProperty("options").GetArrayLength());

        var fifth = results[4];
        AssertNullQuestionWithError(fifth, 422);
        var single422 = await client.PostAsJsonAsync(
            SinglePath,
            new { wordId = data.WordId, bookId = data.DisabledBookId },
            TestContext.Current.CancellationToken);
        var problem422 = await HttpFailureAssertions.AssertFailureAsync(
            single422, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(
            problem422.GetProperty("title").GetString(),
            fifth.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task EmptyOrMissingItems_Returns400Envelope()
    {
        using var client = _factory.CreateClient();

        var empty = await client.PostAsJsonAsync(
            BatchPath,
            new { items = Array.Empty<object>() },
            TestContext.Current.CancellationToken);
        var emptyRoot = await HttpFailureAssertions.AssertFailureAsync(
            empty, HttpStatusCode.BadRequest);
        Assert.Equal(
            "At least one item is required.",
            emptyRoot.GetProperty("message").GetString());

        var missing = await client.PostAsJsonAsync(
            BatchPath,
            new { },
            TestContext.Current.CancellationToken);
        var missingRoot = await HttpFailureAssertions.AssertFailureAsync(
            missing, HttpStatusCode.BadRequest);
        Assert.Equal(
            "At least one item is required.",
            missingRoot.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ItemsBeyondTheLimit_Returns400EnvelopeAndTheLimitAnswers()
    {
        var data = await SeedAsync(_factory);
        using var client = _factory.CreateClient();

        var items = Enumerable
            .Range(0, VocabularyHttpEndpoints.MaxQuestionBatchItems + 1)
            .Select(_ => new { wordId = data.WordId, bookId = data.BookId })
            .ToArray();

        var refused = await client.PostAsJsonAsync(
            BatchPath,
            new { items },
            TestContext.Current.CancellationToken);
        var refusedRoot = await HttpFailureAssertions.AssertFailureAsync(
            refused, HttpStatusCode.BadRequest);
        Assert.Equal(
            $"A batch can contain at most {VocabularyHttpEndpoints.MaxQuestionBatchItems} items.",
            refusedRoot.GetProperty("message").GetString());

        var accepted = await client.PostAsJsonAsync(
            BatchPath,
            new { items = items[..^1] },
            TestContext.Current.CancellationToken);
        await AssertOkAsync(accepted);
        using var body = JsonDocument.Parse(
            await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var results = body.RootElement.GetProperty("data").GetProperty("results");
        Assert.Equal(VocabularyHttpEndpoints.MaxQuestionBatchItems, results.GetArrayLength());
        var index = 0;
        foreach (var row in results.EnumerateArray())
        {
            Assert.Equal(index, row.GetProperty("index").GetInt32());
            Assert.True(row.GetProperty("question").ValueKind == JsonValueKind.Object);
            index++;
        }
    }

    [Theory]
    [InlineData("{ \"items\": ", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{ \"items\": \"not-a-list\" }", "application/json")]
    [InlineData("{ \"items\": [ \"not-an-object\" ] }", "application/json")]
    [InlineData("{ \"items\": [] }", "text/plain")]
    public async Task BodyThatIsNotValidJson_Returns400Envelope(string body, string contentType)
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, contentType);

        var response = await client.PostAsync(BatchPath, content, TestContext.Current.CancellationToken);

        var root = await HttpFailureAssertions.AssertFailureAsync(
            response, HttpStatusCode.BadRequest);
        Assert.Equal(
            "The request body is not valid JSON.",
            root.GetProperty("message").GetString());
    }

    // TestServer does not enforce request size limits, so this runs on its own
    // Kestrel-hosted factory rather than the shared one.
    [Fact]
    public async Task BodyOverOneMebibyte_Returns413OnKestrel()
    {
        using var factory = new VocabularyWebApplicationFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var data = await SeedAsync(factory);
        // The base-class client picks up the real Kestrel binding's address.
        using var client = ((WebApplicationFactory<Program>)factory).CreateClient();

        var overLimit = PaddedBatch(
            new { wordId = data.WordId, bookId = data.BookId },
            VocabularyHttpEndpoints.MaxBatchRequestBytes + 1);

        using (var content = new ByteArrayContent(overLimit))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await client.PostAsync(
                BatchPath, content, TestContext.Current.CancellationToken);
            var problem = await HttpFailureAssertions.AssertFailureAsync(
                response, HttpStatusCode.RequestEntityTooLarge);
            Assert.Equal("vocabulary.request_too_large", problem.GetProperty("errorCode").GetString());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, BatchPath))
        {
            // No Content-Length, so the limit has to be enforced while reading.
            request.Content = new StreamContent(new NonSeekableStream(overLimit));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TransferEncodingChunked = true;
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var problem = await HttpFailureAssertions.AssertFailureAsync(
                response, HttpStatusCode.RequestEntityTooLarge);
            Assert.Equal("vocabulary.request_too_large", problem.GetProperty("errorCode").GetString());
        }

        // At the limit the body parses and the batch answers per item.
        var atLimit = PaddedBatch(
            new { wordId = data.WordId, bookId = data.BookId },
            VocabularyHttpEndpoints.MaxBatchRequestBytes);
        using (var content = new ByteArrayContent(atLimit))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await client.PostAsync(
                BatchPath, content, TestContext.Current.CancellationToken);
            await AssertOkAsync(response);
        }
    }

    [Fact]
    public async Task CancelledRequest_StopsRemainingItems()
    {
        var data = await SeedAsync(_factory);
        var firstItemEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        using var configured = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVocabularyBookRepository>();
                services.AddScoped<IVocabularyBookRepository>(sp =>
                    new GatedBookRepository(
                        new VocabularyBookRepository(
                            sp.GetRequiredService<VocabularyDbContext>()),
                        () =>
                        {
                            if (Interlocked.Increment(ref calls) == 1)
                            {
                                firstItemEntered.TrySetResult();
                            }

                            return Task.CompletedTask;
                        },
                        release.Task));
            }));
        using var client = configured.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var items = Enumerable.Range(0, 3)
            .Select(_ => new { wordId = data.WordId, bookId = data.BookId });
        var sending = client.PostAsJsonAsync(
            BatchPath, new { items }, cancellation.Token);

        // The first item is inside its database read; cancelling now must stop
        // the two behind it rather than answer items nobody will read.
        await firstItemEntered.Task;
        cancellation.Cancel();
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.True(body.RootElement.TryGetProperty("data", out _));
    }

    private static void AssertNullQuestionWithError(JsonElement row, int status)
    {
        Assert.Equal(JsonValueKind.Null, row.GetProperty("question").ValueKind);
        var error = row.GetProperty("error");
        Assert.Equal(status, error.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    private static byte[] PaddedBatch(object item, long length)
    {
        // One valid item of ordinary size, followed by whitespace to the asked
        // length: trailing blanks are valid JSON, so only the size can refuse.
        var json = JsonSerializer.Serialize(new { items = new[] { item } });
        var body = new byte[length];
        Array.Fill(body, (byte)' ');
        Encoding.UTF8.GetBytes(json).CopyTo(body, 0);
        return body;
    }

    private sealed record SeedData(
        string BookId,
        string DisabledBookId,
        string WordId,
        string MeaningId);

    /// <summary>
    /// One enabled book holding the asked word and four distractors, and one
    /// disabled book: enough for a valid item, a whole-book draw, and the
    /// single 422 a disabled book answers.
    /// </summary>
    private static async Task<SeedData> SeedAsync(VocabularyWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var bookId = $"bq-book-{suffix}";
        var disabledBookId = $"bq-disabled-{suffix}";
        var wordId = $"bq-word-target-{suffix}";
        var now = DateTimeOffset.UtcNow;
        context.VocabularyBooks.Add(new VocabularyBookEntity
        {
            Id = bookId,
            BookName = $"Batch Question Book {suffix}",
            Status = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.VocabularyBooks.Add(new VocabularyBookEntity
        {
            Id = disabledBookId,
            BookName = $"Batch Question Disabled Book {suffix}",
            Status = false,
            CreatedAt = now,
            UpdatedAt = now
        });

        var meaningId = $"bq-meaning-target-{suffix}";
        context.Vocabularies.Add(new VocabularyEntity
        {
            Id = wordId,
            Word = $"target-{suffix}",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.VocabularyMeanings.Add(new VocabularyMeaningEntity
        {
            Id = meaningId,
            VocabularyId = wordId,
            BookId = bookId,
            Meaning = $"target meaning-{suffix}",
            CreatedAt = now,
            UpdatedAt = now
        });

        foreach (var (word, meaning) in new[]
                 {
                     ("apple", "fruit"),
                     ("banana", "yellow fruit"),
                     ("cherry", "red fruit"),
                     ("date", "sweet fruit")
                 })
        {
            var memberWordId = $"bq-word-{word}-{suffix}";
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = memberWordId,
                Word = $"{word}-{suffix}",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = $"bq-meaning-{word}-{suffix}",
                VocabularyId = memberWordId,
                BookId = bookId,
                Meaning = $"{meaning}-{suffix}",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await context.SaveChangesAsync();
        return new SeedData(bookId, disabledBookId, wordId, meaningId);
    }

    /// <summary>
    /// Wraps the real book repository, counting every read and holding the
    /// first one until the test lets it go, so the observer can tell which
    /// item the batch was working on when the request was cancelled.
    /// </summary>
    private sealed class GatedBookRepository(
        IVocabularyBookRepository inner,
        Func<Task> onGetById,
        Task release) : IVocabularyBookRepository
    {
        public async Task<VocabularyBookModel?> GetByIdAsync(string id)
        {
            await onGetById();
            await release;
            return await inner.GetByIdAsync(id);
        }

        public Task<List<VocabularyBookModel>> GetAllAsync() => inner.GetAllAsync();
        public Task<List<VocabularyBookModel>> GetActiveAsync() => inner.GetActiveAsync();
        public Task<(List<VocabularyBookModel> Items, int TotalCount)> SearchAsync(
            string? keyword, int page, int size) => inner.SearchAsync(keyword, page, size);
        public Task<List<VocabularyBookModel>> GetByCategoryAsync(
            string? category, string? grade) => inner.GetByCategoryAsync(category, grade);
        public Task<List<string>> GetDistinctCategoriesAsync() => inner.GetDistinctCategoriesAsync();
        public Task<List<string>> GetDistinctEducationLevelsAsync()
            => inner.GetDistinctEducationLevelsAsync();
        public Task<List<string>> GetDistinctGradesAsync() => inner.GetDistinctGradesAsync();
        public Task<List<string>> GetDistinctGradesByEducationLevelAsync(
            string educationLevel) => inner.GetDistinctGradesByEducationLevelAsync(educationLevel);
        public Task<bool> HasMeaningsAsync(string bookId) => inner.HasMeaningsAsync(bookId);
        public Task<(List<VocabularyModel> Items, int TotalCount)> GetWordsAsync(
            string bookId, int page, int size) => inner.GetWordsAsync(bookId, page, size);
        public Task AddAsync(VocabularyBookModel model) => inner.AddAsync(model);
        public Task UpdateAsync(VocabularyBookModel model) => inner.UpdateAsync(model);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
    }

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
