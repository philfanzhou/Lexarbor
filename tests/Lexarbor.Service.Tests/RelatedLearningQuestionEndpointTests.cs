using System.Net;
using System.Net.Http.Json;
using System.Text;
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
using static Lexarbor.Service.Tests.LearningQuestionEndpointTests;

namespace Lexarbor.Service.Tests;

public sealed class RelatedLearningQuestionEndpointTests
{
    private const string Route = "/api/vocabulary/learning-question";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(3, "FormChoice")]
    [InlineData(4, "RelationChoice")]
    [InlineData(5, "FormFill")]
    public async Task ConfirmedRelationProducesEachNewResponse(int first, string type)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = PickFirst(factory, first);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services);
        var data = await Success(await client.PostAsJsonAsync(Route, Request("went", "go", "past_tense"), Ct));
        Assert.Equal(type, data.GetProperty("type").GetString());

        if (type == "FormChoice")
        {
            Assert.Equal(new[] { "options", "prompt", "relationType", "type" }, Fields(data));
            Assert.Equal("go", data.GetProperty("prompt").GetString());
            Assert.Equal("past_tense", data.GetProperty("relationType").GetString());
            Assert.Equal("went", Correct(data));
            Assert.All(Wrong(data), choice => Assert.Contains(choice, new[] { "go", "gone", "going", "goes" }));
        }
        else if (type == "RelationChoice")
        {
            Assert.Equal(new[] { "anchorWord", "options", "prompt", "type" }, Fields(data));
            Assert.Equal("went", data.GetProperty("prompt").GetString());
            Assert.Equal("go", data.GetProperty("anchorWord").GetString());
            Assert.Equal("past_tense", Correct(data));
            Assert.Equal(new[] { "ing_form", "past_participle", "third_person_singular" }, Wrong(data).Order());
        }
        else
        {
            Assert.Equal(new[] { "anchorWord", "answerWord", "pattern", "prompt", "relationType", "type" }, Fields(data));
            Assert.Equal("went", data.GetProperty("answerWord").GetString());
            Assert.Equal("go", data.GetProperty("anchorWord").GetString());
            Assert.Equal("past_tense", data.GetProperty("relationType").GetString());
            Assert.Equal("去了", data.GetProperty("prompt").GetString());
            Assert.Equal("w___", Pattern(data));
        }
    }

    [Theory]
    [InlineData("happy", "happiness", "derived_noun", "happ_____", 5)]
    [InlineData("stop", "stopped", "past_tense", "stop___", 5)]
    [InlineData("happy", "unhappy", "derived_adjective", "__happy", 5)]
    [InlineData("𐐀", "𐐨-Z", "derived_adjective", "𐐨-_", 4)]
    public async Task FormFillKeepsTheLongerCommonEndAndPreservesNonletters(string anchor, string target, string type, string expected, int first)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = PickFirst(factory, first);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            AddWord(db, "anchor", anchor, "原词", "other");
            AddWord(db, "target", target, "目标词", "book");
            db.VocabularyRelations.Add(Edge("anchor", "target", type));
            await db.SaveChangesAsync(Ct);
        }
        var data = await Success(await client.PostAsJsonAsync(Route, Request("target", "anchor", type), Ct));
        Assert.Equal("FormFill", data.GetProperty("type").GetString());
        Assert.Equal(target, data.GetProperty("answerWord").GetString());
        Assert.Equal(expected, Pattern(data));
    }

    [Theory]
    [InlineData(0, "EnglishToChinese", "已经去过", "正在去")]
    [InlineData(1, "ChineseToEnglish", "going", "goes")]
    public async Task ReviewUsesSiblingsAcrossBooksAndFillsFromTheRequestedUnit(int first, string type, string firstOption, string secondOption)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = PickFirst(factory, first);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services, translation: true);
        var request = new { wordId = "went", bookId = "book", meaningId = "m-went", unitId = "unit", sameEntryKind = true, mode = "review" };
        var data = await Success(await client.PostAsJsonAsync(Route, request, Ct));
        Assert.Equal(type, data.GetProperty("type").GetString());
        AssertStructure(data);
        Assert.Equal(new[] { firstOption, secondOption, first == 0 ? "本单元" : "local" }.Order(), Wrong(data).Order());
    }

    [Fact]
    public async Task ReviewKeepsFamilyWordsWhenFallbackDefinitionsOverlap()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = PickFirst(factory, 0);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services, translation: true);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.VocabularyMeanings.Where(item => item.Id == "m-local").ExecuteUpdateAsync(
                update => update.SetProperty(item => item.Meaning, "已经去过"), Ct);
            AddMeaning(db, "alternative-sibling", "gone", "已离去", "other");
            await db.SaveChangesAsync(Ct);
        }
        var request = new { wordId = "went", bookId = "book", meaningId = "m-went", unitId = "unit", mode = "review" };
        var data = await Success(await client.PostAsJsonAsync(Route, request, Ct));
        Assert.Equal("EnglishToChinese", data.GetProperty("type").GetString());
        Assert.Equal(new[] { "已经去过", "已离去", "正在去" }.Order(), Wrong(data).Order());
    }

    [Fact]
    public async Task ReverseAndStaleRelationFallBackAndNewOnlyUsesTheOriginalPool()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = PickFirst(factory, 6);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services, translation: true);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyRelations.Add(Edge("go", "goer", "derived_noun"));
            AddWord(db, "goer", "goer", "常去的人", "other");
            await db.SaveChangesAsync(Ct);
        }
        var reverse = await Success(await client.PostAsJsonAsync(Route, Request("go", "went", "past_tense", book: "other"), Ct));
        Assert.Equal("EnglishToChinese", reverse.GetProperty("type").GetString());
        AssertStructure(reverse);
        foreach (var anchor in new[] { "missing", "pending" })
        {
            var stale = await Success(await client.PostAsJsonAsync(Route, Request("went", anchor, "past_tense"), Ct));
            Assert.Equal("EnglishToChinese", stale.GetProperty("type").GetString());
            Assert.Equal(new[] { "已经去过", "正在去", "常去的人" }.Order(), Wrong(stale).Order());
        }
        var initial = await Success(await client.PostAsJsonAsync(Route, Request("went", "go", "past_tense", mode: "new"), Ct));
        Assert.Equal("EnglishToChinese", initial.GetProperty("type").GetString());
        Assert.DoesNotContain("已经去过", Wrong(initial));
        Assert.DoesNotContain("正在去", Wrong(initial));
        var legacy = await Success(await client.PostAsJsonAsync("/api/vocabulary/question",
            new { wordId = "went", bookId = "book", meaningId = "m-went", chineseToEnglish = false }, Ct));
        Assert.DoesNotContain(legacy.GetProperty("options").EnumerateArray(), option => option.GetProperty("meaning").GetString() == "已经去过");
    }

    [Theory]
    [InlineData(3, "past_tense")]
    [InlineData(4, "past_participle")]
    public async Task AmbiguousFormsAndRelationsCannotMakeMultipleCorrectOptions(int first, string relationType)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = PickFirst(factory, first);
        using var client = host.CreateClient();
        await client.UseCatalogKeyAsync(host);
        await Seed(host.Services);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            if (first == 3)
            {
                db.VocabularyRelations.Add(Edge("go", "gone", "past_tense"));
                db.Vocabularies.Add(new VocabularyEntity { Id = "duplicate-gone", Word = "GONE" });
                db.VocabularyRelations.Add(Edge("go", "duplicate-gone", "derived_noun"));
            }
            else
            {
                AddWord(db, "cut", "cut", "切", "book");
                db.VocabularyRelations.AddRange(Edge("cut", "cut", "past_tense"), Edge("cut", "cut", "past_participle"));
            }
            await db.SaveChangesAsync(Ct);
        }
        var data = await Success(await client.PostAsJsonAsync(Route,
            first == 3 ? Request("went", "go", relationType) : Request("cut", "cut", relationType), Ct));
        if (first == 3)
        {
            Assert.Equal("FormChoice", data.GetProperty("type").GetString());
            Assert.Equal(new[] { "go", "goes", "going" }, Wrong(data).Order());
        }
        else Assert.Equal("FillLetters", data.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"past_tense\"}")]
    [InlineData("{\"sourceWordId\":\"go\",\"type\":\"unknown\"}")]
    [InlineData("{\"sourceWordId\":\" \",\"type\":\"past_tense\"}")]
    public async Task InvalidRelationIsRejectedEvenInNew(string relation)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.UseCatalogKeyAsync(factory);
        var body = "{\"wordId\":\"went\",\"bookId\":\"book\",\"meaningId\":\"m-went\",\"mode\":\"new\",\"relation\":" + relation + "}";
        await Error(await client.PostAsync(Route, new StringContent(body, Encoding.UTF8, "application/json"), Ct),
            HttpStatusCode.BadRequest, "QUESTION_INVALID_REQUEST");
    }

    private static WebApplicationFactory<Program> PickFirst(VocabularyWebApplicationFactory factory, int first) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ =>
            {
                var initial = true;
                return new LearningQuestionGenerator(count =>
                {
                    if (!initial) return count - 1;
                    initial = false;
                    return first % count;
                });
            });
        }));

    private static object Request(string target, string anchor, string type, string mode = "review", string book = "book") => new
    {
        wordId = target,
        bookId = book,
        meaningId = "m-" + target,
        mode,
        relation = new { sourceWordId = anchor, type }
    };

    private static async Task Seed(IServiceProvider services, bool translation = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "book", BookName = "Book", Status = true },
            new VocabularyBookEntity { Id = "other", BookName = "Other", Status = true }, new VocabularyBookEntity { Id = "disabled", BookName = "Disabled" });
        db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
        AddWord(db, "went", "went", "去了", "book");
        AddWord(db, "go", "go", "去过", "other");
        AddWord(db, "gone", "gone", "已经去过", "other");
        AddWord(db, "going", "going", "正在去", "other");
        AddWord(db, "goes", "goes", "移动", "other");
        db.VocabularyRelations.AddRange(Edge("go", "went", "past_tense"), Edge("go", "gone", "past_participle"),
            Edge("go", "going", "ing_form"), Edge("go", "goes", "third_person_singular"));
        if (translation)
        {
            AddWord(db, "local", "local", "本单元", "book");
            AddWord(db, "regular-1", "regular-one", "普通一", "book");
            AddWord(db, "regular-2", "regular-two", "普通二", "book");
            AddWord(db, "pending", "pending", "待确认", "other");
            AddWord(db, "disabled-candidate", "disabled-candidate", "停用", "disabled");
            AddWord(db, "alias-gone", "GONE", "去了", "other");
            AddMeaning(db, "alternate-go", "went", "去过", "other");
            AddMeaning(db, "alternate-goes", "went", "移动", "other");
            AddMeaning(db, "synonym-go", "go", "去了", "other");
            db.VocabularyMeaningUnits.Add(new() { UnitId = "unit", BookId = "book", MeaningId = "m-local" });
            db.VocabularyRelations.AddRange(Edge("pending", "went", "past_tense", confirmed: false), Edge("go", "disabled-candidate", "derived_noun"));
        }
        await db.SaveChangesAsync(Ct);
    }

    private static void AddWord(VocabularyDbContext db, string id, string text, string meaning, string book)
    {
        db.Vocabularies.Add(new() { Id = id, Word = text });
        AddMeaning(db, "m-" + id, id, meaning, book);
    }

    private static void AddMeaning(VocabularyDbContext db, string id, string word, string meaning, string book) =>
        db.VocabularyMeanings.Add(new() { Id = id, VocabularyId = word, BookId = book, Meaning = meaning });

    private static VocabularyRelationEntity Edge(string source, string target, string type, bool confirmed = true) => new()
    {
        Id = source + ":" + target + ":" + type,
        SourceWordId = source,
        TargetWordId = target,
        Type = type,
        OriginKind = "manual",
        Status = confirmed ? "confirmed" : "needs_review",
        ReviewReason = confirmed ? null : "new_relation",
        LastConfirmedAtUnixMs = confirmed ? 0 : null
    };

    private static IEnumerable<string> Fields(JsonElement data) => data.EnumerateObject().Select(item => item.Name).Order();
    private static string Pattern(JsonElement data) => string.Concat(data.GetProperty("pattern").EnumerateArray().Select(item => item.GetString() ?? "_"));
    private static string? Correct(JsonElement data) => Assert.Single(data.GetProperty("options").EnumerateArray(), IsCorrect).GetProperty("text").GetString();
    private static IEnumerable<string?> Wrong(JsonElement data) => data.GetProperty("options").EnumerateArray().Where(item => !IsCorrect(item)).Select(item => item.GetProperty("text").GetString());
    private static bool IsCorrect(JsonElement item) => item.GetProperty("isCorrect").GetBoolean();
}
