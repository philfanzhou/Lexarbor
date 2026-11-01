using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public class PublicApiCompatibilityTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private readonly VocabularyWebApplicationFactory _factory;

    public PublicApiCompatibilityTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Detail_RemainsAnonymousWithExistingEnvelopeShape()
    {
        var data = await SeedCompleteBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/vocabulary/{data.WordId}?bookId={data.BookId}",
                TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var result = document.RootElement.GetProperty("data");
            Assert.True(result.TryGetProperty("id", out _));
            Assert.True(result.TryGetProperty("word", out _));
            Assert.Equal("/test-uk/", result.GetProperty("phoneticUk").GetString());
            Assert.Equal("/test-us/", result.GetProperty("phoneticUs").GetString());
            Assert.Equal(JsonValueKind.Array, result.GetProperty("meanings").ValueKind);
            var meaning = Assert.Single(result.GetProperty("meanings").EnumerateArray());
            Assert.Equal(new[] { "bookId", "example", "id", "meaning", "partOfSpeech", "pronunciation", "vocabularyId" },
                meaning.EnumerateObject().Select(property => property.Name).Order());
            var pronunciation = meaning.GetProperty("pronunciation");
            Assert.Equal(new[] { "uk", "us" }, pronunciation.EnumerateObject().Select(property => property.Name).Order());
            Assert.All(pronunciation.EnumerateObject(), accent => Assert.Equal(new[] { "phonetic", "source" },
                accent.Value.EnumerateObject().Select(property => property.Name).Order()));
            Assert.Equal("/specific-uk/", pronunciation.GetProperty("uk").GetProperty("phonetic").GetString());
            Assert.Equal("meaning", pronunciation.GetProperty("uk").GetProperty("source").GetString());
            Assert.Equal("/test-us/", pronunciation.GetProperty("us").GetProperty("phonetic").GetString());
            Assert.Equal("default", pronunciation.GetProperty("us").GetProperty("source").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyImports_CannotWriteOrClearMeaningPhonetics(bool includeReadOnlyFields)
    {
        var data = await SeedCompleteBookAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", _factory.CreateToken("admin"));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var original = await db.VocabularyMeanings.AsNoTracking().SingleAsync(row => row.VocabularyId == data.WordId, TestContext.Current.CancellationToken);
        var word = await db.Vocabularies.AsNoTracking().SingleAsync(row => row.Id == data.WordId, TestContext.Current.CancellationToken);
        var meaning = new Dictionary<string, object?>
        {
            ["id"] = original.Id,
            ["bookId"] = original.BookId,
            ["vocabularyId"] = original.VocabularyId,
            ["meaning"] = original.Meaning
        };
        if (includeReadOnlyFields)
        {
            meaning["phoneticUk"] = "injected";
            meaning["phoneticUs"] = "";
            meaning["pronunciation"] = new { uk = new { phonetic = "injected", source = "meaning" } };
        }
        using var single = await client.PostAsJsonAsync("/admin/vocabulary", new
        {
            word = new { id = word.Id, word = word.Word },
            meaning
        }, TestContext.Current.CancellationToken);
        single.EnsureSuccessStatusCode();
        meaning["word"] = word.Word;
        using var batch = await client.PostAsJsonAsync("/admin/vocabulary/batch", new
        {
            bookId = data.BookId,
            entries = new[] { meaning }
        }, TestContext.Current.CancellationToken);
        batch.EnsureSuccessStatusCode();
        var stored = await db.VocabularyMeanings.AsNoTracking().SingleAsync(row => row.Id == original.Id, TestContext.Current.CancellationToken);
        Assert.Equal("/specific-uk/", stored.PhoneticUk);
        Assert.Equal(" \t ", stored.PhoneticUs);
    }

    [Fact]
    public async Task Search_RemainsAnonymousWithExistingEnvelopeShape()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/api/vocabulary?keyword=compatibility&page=1&size=20",
                TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var result = document.RootElement.GetProperty("data");
            Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
            Assert.True(result.TryGetProperty("totalPage", out _));
            Assert.True(result.TryGetProperty("totalCount", out _));
        }
    }

    [Fact]
    public async Task Question_RemainsAnonymousWithExistingEnvelopeShape()
    {
        var data = await SeedCompleteBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = true
            }, TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var result = document.RootElement.GetProperty("data");
            Assert.True(result.TryGetProperty("word", out _));
            Assert.Equal(4, result.GetProperty("options").GetArrayLength());
        }
    }

    [Fact]
    public async Task Question_WithoutScopeFields_AnswersFourOptionsWithOneCorrect()
    {
        var data = await SeedCompleteBookAsync();
        using var client = _factory.CreateClient();

        // The request a caller sent before the scope fields existed: the
        // direction falls to the server's draw and the answer still holds four
        // distinct options with exactly one correct one.
        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new { wordId = data.WordId, bookId = data.BookId },
            TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var options = document.RootElement.GetProperty("data").GetProperty("options");
            Assert.Equal(4, options.GetArrayLength());
            Assert.Equal(4, options.EnumerateArray()
                .Select(option => option.GetProperty("meaning").GetString())
                .Distinct()
                .Count());
            var correct = 0;
            foreach (var option in options.EnumerateArray())
            {
                correct += option.GetProperty("isCorrect").GetBoolean() ? 1 : 0;
            }

            Assert.Equal(1, correct);
        }
    }

    [Fact]
    public async Task QuestionBatch_RemainsAnonymousWithEnvelopeShape()
    {
        var data = await SeedCompleteBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/questions",
            new
            {
                items = new[]
                {
                    new { wordId = data.WordId, bookId = data.BookId, chineseToEnglish = true }
                }
            },
            TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            // One row per item, in request order, carrying the same question
            // shape the single endpoint answers with.
            var results = document.RootElement.GetProperty("data").GetProperty("results");
            Assert.Equal(JsonValueKind.Array, results.ValueKind);
            var row = Assert.Single(results.EnumerateArray());
            Assert.Equal(0, row.GetProperty("index").GetInt32());
            var question = row.GetProperty("question");
            Assert.True(question.TryGetProperty("word", out _));
            Assert.Equal(4, question.GetProperty("options").GetArrayLength());
        }
    }

    [Fact]
    public async Task Books_RemainsAnonymousWithExistingEnvelopeShape()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/vocabulary-books/all",
            TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            Assert.Equal(
                JsonValueKind.Array,
                document.RootElement
                    .GetProperty("data")
                    .GetProperty("books")
                    .ValueKind);
        }
    }

    [Fact]
    public async Task BookUnits_RemainAnonymousWithEnvelopeShape()
    {
        var data = await SeedCompleteBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/vocabulary-books/{data.BookId}/units",
            TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var units = document.RootElement.GetProperty("data").GetProperty("units");
            Assert.Equal(JsonValueKind.Array, units.ValueKind);
            Assert.All(units.EnumerateArray(), unit => Assert.Equal(
                ["id", "meaningCount", "number", "title", "wordCount"],
                unit.EnumerateObject().Select(property => property.Name).Order()));
        }
    }

    [Fact]
    public async Task BookEntries_RemainAnonymousWithEnvelopeShape()
    {
        var data = await SeedCompleteBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/vocabulary-books/{data.BookId}/entries?page=1&size=20",
            TestContext.Current.CancellationToken);

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var result = document.RootElement.GetProperty("data");
            Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
            Assert.True(result.TryGetProperty("totalCount", out _));
            Assert.True(result.TryGetProperty("totalPage", out _));
            Assert.True(result.TryGetProperty("wordCount", out _));
        }
    }

    private async Task<(string BookId, string WordId)> SeedCompleteBookAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var bookId = $"compat-book-{suffix}";
        var now = DateTimeOffset.UtcNow;
        context.VocabularyBooks.Add(new VocabularyBookEntity
        {
            Id = bookId,
            BookName = $"Compatibility Book {suffix}",
            Status = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        var wordId = string.Empty;
        for (var index = 0; index < 4; index++)
        {
            var currentWordId = $"compat-word-{index}-{suffix}";
            if (index == 0)
            {
                wordId = currentWordId;
            }

            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = currentWordId,
                Word = $"compatibility-{index}-{suffix}",
                PhoneticUk = "/test-uk/",
                PhoneticUs = "/test-us/",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = $"compat-meaning-{index}-{suffix}",
                VocabularyId = currentWordId,
                BookId = bookId,
                Meaning = $"compatibility meaning {index} {suffix}",
                PhoneticUk = index == 0 ? "/specific-uk/" : null,
                PhoneticUs = index == 0 ? " \t " : null,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await context.SaveChangesAsync();
        return (bookId, wordId);
    }

    private static async Task<JsonDocument> AssertPublicSuccessAsync(
        HttpResponseMessage response)
    {
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.True(document.RootElement.TryGetProperty("data", out _));
        return document;
    }
}
