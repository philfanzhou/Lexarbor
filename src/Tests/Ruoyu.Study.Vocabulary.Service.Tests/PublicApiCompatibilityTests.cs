using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Vocabulary.Database;
using Ruoyu.Study.Vocabulary.Database.Entities;
using Ruoyu.Study.Vocabulary.Service.Tests.TestInfrastructure;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

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
            $"/api/vocabulary/{data.WordId}?bookId={data.BookId}");

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var result = document.RootElement.GetProperty("data");
            result.TryGetProperty("id", out _).Should().BeTrue();
            result.TryGetProperty("word", out _).Should().BeTrue();
            result.GetProperty("meanings").ValueKind.Should().Be(JsonValueKind.Array);
        }
    }

    [Fact]
    public async Task Search_RemainsAnonymousWithExistingEnvelopeShape()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/api/vocabulary?keyword=compatibility&page=1&size=20");

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var result = document.RootElement.GetProperty("data");
            result.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
            result.TryGetProperty("totalPage", out _).Should().BeTrue();
            result.TryGetProperty("totalCount", out _).Should().BeTrue();
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
            });

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            var result = document.RootElement.GetProperty("data");
            result.TryGetProperty("word", out _).Should().BeTrue();
            result.GetProperty("options").GetArrayLength().Should().Be(4);
        }
    }

    [Fact]
    public async Task Books_RemainsAnonymousWithExistingEnvelopeShape()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/vocabulary-books/all");

        var document = await AssertPublicSuccessAsync(response);
        using (document)
        {
            document.RootElement
                .GetProperty("data")
                .GetProperty("books")
                .ValueKind.Should().Be(JsonValueKind.Array);
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
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.TryGetProperty("data", out _).Should().BeTrue();
        return document;
    }
}
