using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;

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
        }
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
