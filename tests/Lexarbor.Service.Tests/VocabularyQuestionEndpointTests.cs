using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The HTTP surface of the question endpoint's optional scope fields: the ids
/// the response reports, the envelope of the new failure cases, and the
/// untouched behaviour of a request that sends none of them.
/// </summary>
public class VocabularyQuestionEndpointTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private readonly VocabularyWebApplicationFactory _factory;

    public VocabularyQuestionEndpointTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Question_WithMeaningUnitAndKind_ReportsIdsAndScopedOptions()
    {
        var data = await SeedScopedBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = false,
                meaningId = data.SecondMeaningId,
                unitId = data.UnitId,
                sameEntryKind = true
            }, TestContext.Current.CancellationToken);

        await AssertOkAsync(response);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var result = body.RootElement.GetProperty("data");

        // The top level reports what was actually asked: which word, which
        // meaning, and which direction — the direction the request named here.
        Assert.Equal(data.WordId, result.GetProperty("wordId").GetString());
        Assert.Equal(data.SecondMeaningId, result.GetProperty("meaningId").GetString());
        Assert.Equal(data.WordText, result.GetProperty("word").GetString());
        Assert.False(result.GetProperty("chineseToEnglish").GetBoolean());

        var options = result.GetProperty("options");
        Assert.Equal(4, options.GetArrayLength());
        var correct = options.EnumerateArray()
            .Where(option => option.GetProperty("isCorrect").GetBoolean())
            .ToList();
        Assert.Single(correct);
        Assert.Equal(data.WordId, correct[0].GetProperty("wordId").GetString());
        Assert.Equal(data.SecondMeaningId, correct[0].GetProperty("meaningId").GetString());
        Assert.Equal(data.SecondMeaningText, correct[0].GetProperty("meaning").GetString());

        // Every distractor is a meaning of a phrase-classified unit member and
        // names both ids, so a caller can record which word confused the
        // learner.
        var phraseMeaningIds = data.PhraseMeanings.ToHashSet(StringComparer.Ordinal);
        var phraseWordIds = data.PhraseWords.ToHashSet(StringComparer.Ordinal);
        foreach (var option in options.EnumerateArray())
        {
            if (option.GetProperty("isCorrect").GetBoolean())
            {
                continue;
            }

            var meaningId = option.GetProperty("meaningId").GetString();
            var optionWordId = option.GetProperty("wordId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(meaningId));
            Assert.False(string.IsNullOrWhiteSpace(optionWordId));
            Assert.Contains(meaningId, phraseMeaningIds);
            Assert.Contains(optionWordId, phraseWordIds);
        }
    }

    [Fact]
    public async Task Question_ChineseToEnglish_DistractorMeaningIdsAreNull()
    {
        var data = await SeedScopedBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = true,
                meaningId = data.FirstMeaningId,
                unitId = data.UnitId
            }, TestContext.Current.CancellationToken);

        await AssertOkAsync(response);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var result = body.RootElement.GetProperty("data");

        Assert.True(result.GetProperty("chineseToEnglish").GetBoolean());
        Assert.Equal(data.FirstMeaningText, result.GetProperty("word").GetString());
        var options = result.GetProperty("options");
        Assert.Equal(4, options.GetArrayLength());
        foreach (var option in options.EnumerateArray())
        {
            // A word-level option carries no single meaning, so only the
            // correct one — the asked meaning — reports one.
            Assert.False(string.IsNullOrWhiteSpace(option.GetProperty("wordId").GetString()));
            if (option.GetProperty("isCorrect").GetBoolean())
            {
                Assert.Equal(data.FirstMeaningId, option.GetProperty("meaningId").GetString());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, option.GetProperty("meaningId").ValueKind);
            }
        }
    }

    [Fact]
    public async Task Question_UnknownMeaningId_Returns404Envelope()
    {
        var data = await SeedScopedBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = true,
                meaningId = $"missing-{Guid.NewGuid():N}"
            }, TestContext.Current.CancellationToken);

        await HttpFailureAssertions.AssertFailureAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Question_UnknownUnitId_Returns404Envelope()
    {
        var data = await SeedScopedBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = true,
                unitId = $"missing-{Guid.NewGuid():N}"
            }, TestContext.Current.CancellationToken);

        await HttpFailureAssertions.AssertFailureAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Question_UnitOfAnotherBook_Returns404Envelope()
    {
        var data = await SeedScopedBookAsync();
        var other = await SeedScopedBookAsync();
        using var client = _factory.CreateClient();

        // The unit exists, but not in the requested book — the same 404 a
        // missing one gets, so the other book's ids reveal nothing.
        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = true,
                unitId = other.UnitId
            }, TestContext.Current.CancellationToken);

        await HttpFailureAssertions.AssertFailureAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Question_UnitWithTooFewCandidates_Returns422Envelope()
    {
        var data = await SeedScopedBookAsync();
        using var client = _factory.CreateClient();

        // The named meaning holds no position of the unit, so its kind is word
        // there, and the unit's word-kind pool counts two members. Asking for
        // same-kind distractors then finds fewer than three and the endpoint
        // refuses rather than widening to the book.
        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new
            {
                wordId = data.WordId,
                bookId = data.BookId,
                chineseToEnglish = true,
                meaningId = data.FirstMeaningId,
                unitId = data.UnitId,
                sameEntryKind = true
            }, TestContext.Current.CancellationToken);

        await HttpFailureAssertions.AssertFailureAsync(response, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Question_WithoutNewFields_AnswersFourOptionsWithOneCorrect()
    {
        var data = await SeedScopedBookAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/vocabulary/question",
            new { wordId = data.WordId, bookId = data.BookId },
            TestContext.Current.CancellationToken);

        await AssertOkAsync(response);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var result = body.RootElement.GetProperty("data");

        // The request a pre-scope caller sends keeps its shape and answer: the
        // direction falls back to the server's draw, the distractors come from
        // the whole book, and the new id fields are filled in rather than
        // changing the old ones.
        Assert.Equal(data.WordId, result.GetProperty("wordId").GetString());
        Assert.Equal(4, result.GetProperty("options").GetArrayLength());
        var options = result.GetProperty("options");
        var correct = 0;
        foreach (var option in options.EnumerateArray())
        {
            correct += option.GetProperty("isCorrect").GetBoolean() ? 1 : 0;
        }

        Assert.Equal(1, correct);
    }

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.True(body.RootElement.TryGetProperty("data", out _));
    }

    private sealed record QuestionSeedData(
        string BookId,
        string WordId,
        string WordText,
        string FirstMeaningId,
        string FirstMeaningText,
        string SecondMeaningId,
        string SecondMeaningText,
        string UnitId,
        string[] PhraseWords,
        string[] PhraseMeanings);

    /// <summary>
    /// One book whose unit holds three phrase members and two unclassified
    /// ones, plus book members no unit holds; the asked word sits outside the
    /// unit and carries two meanings. Every text carries the run's suffix,
    /// because the test database is shared and word spellings are unique in it.
    /// </summary>
    private async Task<QuestionSeedData> SeedScopedBookAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var bookId = $"qbook-{suffix}";
        var unitId = $"qunit-{suffix}";
        var wordId = $"qword-apple-{suffix}";
        var wordText = $"apple-{suffix}";
        var firstMeaningText = $"fruit-{suffix}";
        var secondMeaningText = $"pome fruit-{suffix}";
        var now = DateTimeOffset.UtcNow;
        context.VocabularyBooks.Add(new VocabularyBookEntity
        {
            Id = bookId,
            BookName = $"Question Book {suffix}",
            Status = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.VocabularyBookUnits.Add(new VocabularyBookUnitEntity
        {
            Id = unitId,
            BookId = bookId,
            Number = 1,
            Title = "Unit 1",
            CreatedAt = now,
            UpdatedAt = now
        });

        var firstMeaningId = AddWord(context, bookId, wordId, wordText, firstMeaningText, now);
        var secondMeaningId = AddMeaning(context, bookId, wordId, secondMeaningText, now);
        // The named meaning of the scoped test is phrase-classified in the
        // unit, so the kind filter keeps the unit's phrases as its pool.
        context.VocabularyMeaningUnits.Add(new VocabularyMeaningUnitEntity
        {
            UnitId = unitId,
            MeaningId = secondMeaningId,
            BookId = bookId,
            Section = string.Empty,
            EntryKind = "phrase"
        });

        var phrases = new[]
        {
            ("put off", "postpone"),
            ("take off", "remove"),
            ("turn down", "decline")
        };
        var phraseWords = new List<string>();
        var phraseMeanings = new List<string>();
        foreach (var (word, meaning) in phrases)
        {
            var phraseWordId = $"qword-{word.Replace(' ', '-')}-{suffix}";
            phraseWords.Add(phraseWordId);
            phraseMeanings.Add(AddWord(
                context, bookId, phraseWordId, $"{word}-{suffix}", $"{meaning}-{suffix}", now));
            context.VocabularyMeaningUnits.Add(new VocabularyMeaningUnitEntity
            {
                UnitId = unitId,
                MeaningId = phraseMeanings[^1],
                BookId = bookId,
                Section = string.Empty,
                EntryKind = "phrase"
            });
        }

        // Unclassified unit members: they complete the word-kind pool of the
        // unit, which is why the word-target kind test can draw three.
        foreach (var (word, meaning) in new[] { ("banana", "yellow fruit"), ("cherry", "red fruit") })
        {
            var memberWordId = $"qword-{word}-{suffix}";
            var memberMeaningId = AddWord(
                context, bookId, memberWordId, $"{word}-{suffix}", $"{meaning}-{suffix}", now);
            context.VocabularyMeaningUnits.Add(new VocabularyMeaningUnitEntity
            {
                UnitId = unitId,
                MeaningId = memberMeaningId,
                BookId = bookId,
                Section = string.Empty,
                EntryKind = string.Empty
            });
        }

        // Book members no unit holds, so a scope that silently widened to the
        // book would show them.
        AddWord(context, bookId, $"qword-fig-{suffix}", $"fig-{suffix}", $"soft fruit-{suffix}", now);
        AddWord(context, bookId, $"qword-grape-{suffix}", $"grape-{suffix}", $"vine fruit-{suffix}", now);
        AddWord(context, bookId, $"qword-date-{suffix}", $"date-{suffix}", $"sweet fruit-{suffix}", now);

        await context.SaveChangesAsync();
        return new QuestionSeedData(
            bookId,
            wordId,
            wordText,
            firstMeaningId,
            firstMeaningText,
            secondMeaningId,
            secondMeaningText,
            unitId,
            [.. phraseWords],
            [.. phraseMeanings]);
    }

    private static string AddWord(
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
        return AddMeaning(context, bookId, wordId, meaning, now);
    }

    private static string AddMeaning(
        VocabularyDbContext context,
        string bookId,
        string wordId,
        string meaning,
        DateTimeOffset now)
    {
        var meaningId = $"qmeaning-{Guid.NewGuid():N}";
        context.VocabularyMeanings.Add(new VocabularyMeaningEntity
        {
            Id = meaningId,
            VocabularyId = wordId,
            BookId = bookId,
            Meaning = meaning,
            CreatedAt = now,
            UpdatedAt = now
        });
        return meaningId;
    }
}
