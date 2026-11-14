using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Lexarbor.Service.Tests.LearningQuestionEndpointTests;

namespace Lexarbor.Service.Tests;

public sealed class PartOfSpeechChoiceEndpointTests
{
    private const string Route = "/api/vocabulary/learning-question";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("happily", "adverb", "快乐地")]
    [InlineData("happy", "adjective", "快乐的")]
    public async Task ClueCanBeASiblingOrADerivativeOfTheTarget(string target, string partOfSpeech, string meaning)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = Pick(factory, count => count - 1);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services, target: target);

        var data = await Success(await client.PostAsJsonAsync(Route, Request(target), Ct));
        Assert.Equal(new[] { "anchorWord", "options", "partOfSpeech", "prompt", "type" }, data.EnumerateObject().Select(field => field.Name).Order());
        Assert.Equal("PartOfSpeechChoice", data.GetProperty("type").GetString());
        Assert.Equal("happiness", data.GetProperty("anchorWord").GetString());
        Assert.Equal(partOfSpeech, data.GetProperty("partOfSpeech").GetString());
        Assert.Equal(meaning, data.GetProperty("prompt").GetString());
        Assert.Equal(target, Correct(data));
        Assert.Equal(new[] { "cloud", "local", "merrily" }, Wrong(data).Order());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("disabled")]
    [InlineData("unrecognized")]
    [InlineData("same-part-of-speech")]
    [InlineData("missing-target-label")]
    [InlineData("ambiguous-target-label")]
    [InlineData("competing-family-answer")]
    [InlineData("too-few-options")]
    public async Task UnavailablePartOfSpeechQuestionFallsBack(string reason)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = Pick(factory, count => count - 1);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            switch (reason)
            {
                case "pending":
                    await db.VocabularyRelations.Where(edge => edge.TargetWordId == "happiness").ExecuteUpdateAsync(set => set
                        .SetProperty(edge => edge.Status, "needs_review").SetProperty(edge => edge.ReviewReason, "word_changed"), Ct);
                    break;
                case "disabled":
                    db.VocabularyBooks.Add(new() { Id = "disabled", BookName = "Disabled" });
                    await db.SaveChangesAsync(Ct);
                    await db.VocabularyMeanings.Where(row => row.VocabularyId == "happiness").ExecuteUpdateAsync(set => set.SetProperty(row => row.BookId, "disabled"), Ct);
                    break;
                case "unrecognized":
                case "same-part-of-speech":
                    await db.VocabularyMeanings.Where(row => row.VocabularyId == "happiness").ExecuteUpdateAsync(
                        set => set.SetProperty(row => row.PartOfSpeech, reason == "unrecognized" ? "unclear" : "adverb"), Ct);
                    break;
                case "missing-target-label":
                case "ambiguous-target-label":
                    await db.VocabularyMeanings.Where(row => row.Id == "m-happily").ExecuteUpdateAsync(set => set
                        .SetProperty(row => row.PartOfSpeech, reason == "missing-target-label" ? null : "adj./adv."), Ct);
                    break;
                case "competing-family-answer":
                    AddMeaning(db, "matching-sense", "merrily", "other", " 快乐地 ", "adverb");
                    await db.SaveChangesAsync(Ct);
                    break;
                default:
                    await db.VocabularyMeaningUnits.Where(row => row.MeaningId == "m-cloud").ExecuteDeleteAsync(Ct);
                    break;
            }
        }

        var data = await Success(await client.PostAsJsonAsync(Route, Request("happily"), Ct));
        Assert.Equal("FillLetters", data.GetProperty("type").GetString());
        AssertStructure(data);
    }

    [Theory]
    [InlineData("review", false, 3, "PartOfSpeechChoice")]
    [InlineData("new", false, 0, "EnglishToChinese")]
    [InlineData("new", true, 0, "EnglishToChinese")]
    [InlineData("review", true, 6, "PartOfSpeechChoice")]
    [InlineData("review", true, 5, "FormFill")]
    public async Task AutomaticSelectionRespectsModeAndCoexistsWithRelation(string mode, bool useRelation, int first, string expected)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = Pick(factory, count => first % count);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services, extraLocalOption: true);

        var request = Request("happily", mode);
        if (useRelation) request.Add("relation", new { sourceWordId = "happy", type = "derived_adverb" });
        var data = await Success(await client.PostAsJsonAsync(Route, request, Ct));
        Assert.Equal(expected, data.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ManagedSupplementFillsRemainingSlotsFromEnabledBooks()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = Pick(factory, count => count - 1);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.EnrichmentDestinations.Add(new() { Id = "expansion", BookId = "book" });
            await db.VocabularyMeaningUnits.Where(row => row.MeaningId == "m-cloud").ExecuteDeleteAsync(Ct);
            await db.SaveChangesAsync(Ct);
        }

        var data = await Success(await client.PostAsJsonAsync(Route, Request("happily"), Ct));
        Assert.Equal("PartOfSpeechChoice", data.GetProperty("type").GetString());
        Assert.Contains("local", Wrong(data));
        Assert.Contains("merrily", Wrong(data));
        Assert.DoesNotContain("happiness", Wrong(data));
        Assert.DoesNotContain(Wrong(data), text => string.Equals(text, "cheerfully", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, Wrong(data).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ReviewRequiresCatalogAuthorization()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        var request = Request("happily");
        using var unauthenticated = await client.PostAsJsonAsync(Route, request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, await factory.SeedKeyAsync(catalog: false, meaningSubmissions: true));
        using var forbidden = await client.PostAsJsonAsync(Route, request, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static WebApplicationFactory<Program> Pick(VocabularyWebApplicationFactory host, Func<int, int> pick) =>
        host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(pick));
        }));

    private static Dictionary<string, object> Request(string target, string mode = "review") => new()
    {
        ["wordId"] = target,
        ["bookId"] = "book",
        ["meaningId"] = "m-" + target,
        ["mode"] = mode,
        ["unitId"] = "unit",
        ["sameEntryKind"] = true
    };

    private static async Task Seed(IServiceProvider services, bool extraLocalOption = false, string target = "happily")
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "book", BookName = "Book", Status = true },
            new VocabularyBookEntity { Id = "other", BookName = "Other", Status = true });
        db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
        db.Vocabularies.AddRange(new VocabularyEntity { Id = "happily", Word = "happily" }, new VocabularyEntity { Id = "happy", Word = "happy" });
        if (target == "happily") AddMeaning(db, "m-happily", "happily", "book", "快乐地", " ADV. ");
        else AddMeaning(db, "m-happy", "happy", "book", "快乐的", "adj");
        AddWord(db, "happiness", "happiness", "other", "幸福", "noun");
        // Related inflection eligible as a wrong answer, but not as a clue.
        AddWord(db, "merrily", "merrily", "other", "愉快地", "adv");
        // The unrelated spelling is excluded even when its answering sense is
        // on another catalog row outside the requested unit.
        AddWord(db, "local", "local", "book", "本地的", "a.");
        AddWord(db, "cloud", "cloud", "book", "云", "n.");
        AddWord(db, "alias", "CHEERFULLY", "other", " 快乐地 ", "adverb");
        AddWord(db, "candidate", "cheerfully", "book", "欢快地", "adv.");
        AddWord(db, "other-unit", "outside", "book", "范围外", "adverb");
        AddMeaning(db, "candidate-adjective", "alias", "other", "快乐的", "adjective");
        db.VocabularyRelations.AddRange(Link("happy", "happiness", "derived_noun"), Link("happy", "happily", "derived_adverb"),
            Link("happily", "merrily", "past_tense"), Link("happy", "merrily", "past_tense"));
        foreach (var word in new[] { "local", "cloud", "candidate" }) AddPosition(db, word);
        if (extraLocalOption)
        {
            AddWord(db, "extra", "extra", "book", "额外", "n.");
            AddPosition(db, "extra");
        }
        await db.SaveChangesAsync(Ct);
    }

    private static void AddPosition(VocabularyDbContext db, string id) =>
        db.VocabularyMeaningUnits.Add(new() { UnitId = "unit", BookId = "book", MeaningId = "m-" + id });

    private static void AddWord(VocabularyDbContext db, string id, string text, string book, string meaning, string partOfSpeech)
    {
        db.Vocabularies.Add(new() { Id = id, Word = text });
        AddMeaning(db, "m-" + id, id, book, meaning, partOfSpeech);
    }

    private static void AddMeaning(VocabularyDbContext db, string id, string word, string book, string meaning, string partOfSpeech) =>
        db.VocabularyMeanings.Add(new() { Id = id, VocabularyId = word, BookId = book, Meaning = meaning, PartOfSpeech = partOfSpeech });

    private static VocabularyRelationEntity Link(string source, string target, string type) => new()
    {
        Id = $"{source}:{target}",
        SourceWordId = source,
        TargetWordId = target,
        Type = type,
        Status = "confirmed",
        OriginKind = "manual",
        ReviewReason = null,
        LastConfirmedAtUnixMs = 0
    };

    private static string? Correct(JsonElement data) => Assert.Single(data.GetProperty("options").EnumerateArray(), IsCorrect).GetProperty("text").GetString();
    private static IEnumerable<string?> Wrong(JsonElement data) => data.GetProperty("options").EnumerateArray().Where(item => !IsCorrect(item)).Select(item => item.GetProperty("text").GetString());
    private static bool IsCorrect(JsonElement item) => item.GetProperty("isCorrect").GetBoolean();
}
