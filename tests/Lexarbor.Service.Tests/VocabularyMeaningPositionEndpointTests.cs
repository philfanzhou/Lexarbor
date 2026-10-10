using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public class VocabularyMeaningPositionEndpointTests
{
    private const string Path = "/admin/vocabulary-books/A/meanings/m/positions";
    private const string Move = """{"from":{"unitId":"u1","section":"A","entryKind":"phrase"},"to":{"unitId":"u2","section":"B","entryKind":"word"}}""";

    [Fact]
    public async Task MoveAndDelete_UseExactKeysAndPreserveSiblings()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var moved = await client.PutAsync(Path, Json(Move), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        using var replay = await client.PutAsync(Path, Json("""{"from":{"unitId":"u2","section":"B","entryKind":"word"},"to":{"unitId":"u2","section":"B","entryKind":"word"}}"""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal([("u1", "A", "word"), ("u1", "B", "phrase"), ("u2", "", "phrase"), ("u2", "B", "word")], await PositionsAsync(factory));

        using var deleted = await client.DeleteAsync(Path + "/u2?section=B&entryKind=word", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var repeated = await client.DeleteAsync(Path + "/u2?section=B&entryKind=word", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        Assert.Equal([("u1", "A", "word"), ("u1", "B", "phrase"), ("u2", "", "phrase")], await PositionsAsync(factory));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(1, await db.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WordToPhraseAndUnsectionedDelete_AreExact()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var moved = await client.PutAsync(Path, Json("""{"from":{"unitId":"u1","section":"A","entryKind":"word"},"to":{"unitId":"u2","section":"A","entryKind":"phrase"}}"""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        using var deleted = await client.DeleteAsync(Path + "/u2?section=none&entryKind=phrase", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal([("u1", "A", "phrase"), ("u1", "B", "phrase"), ("u2", "A", "phrase")], await PositionsAsync(factory));
    }

    [Theory]
    [InlineData("""{"from":{"unitId":"u1","section":"A","entryKind":"phrase"},"to":{"unitId":"u1","section":"A","entryKind":"word"}}""", 409)]
    [InlineData("""{"from":{"unitId":"u1","section":null,"entryKind":"phrase"},"to":{"unitId":"u1","section":null,"entryKind":"phrase"}}""", 404)]
    [InlineData("""{"from":{"unitId":"u1","section":"A","entryKind":"phrase"},"to":{"unitId":"foreign","section":null,"entryKind":"word"}}""", 404)]
    [InlineData("""{"from":{"unitId":"u1","section":"C","entryKind":"phrase"},"to":{"unitId":"u2","section":null,"entryKind":"word"}}""", 400)]
    [InlineData("""{"from":{"unitId":"u1","section":"A","entryKind":"Word"},"to":{"unitId":"u2","section":null,"entryKind":"word"}}""", 400)]
    [InlineData("""{"from":{"unitId":"u1","section":"A","entryKind":"phrase"},"to":{"unitId":"u2","entryKind":"word"}}""", 400)]
    [InlineData("""{"from":{"unitId":"u1","section":"A","entryKind":"phrase"},"to":{"unitId":"u2","section":null,"entryKind":"word","extra":1}}""", 400)]
    public async Task RefusedMove_HasNoPartialWrites(string body, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var response = await client.PutAsync(Path, Json(body), TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal([("u1", "A", "phrase"), ("u1", "A", "word"), ("u1", "B", "phrase"), ("u2", "", "phrase")], await PositionsAsync(factory));
    }

    [Fact]
    public async Task MoveToPhrase_MeaningWithPartOfSpeech_Returns400WithoutWrites()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.VocabularyMeanings.Where(item => item.Id == "m")
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PartOfSpeech, "v."), TestContext.Current.CancellationToken);
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var response = await client.PutAsync(Path, Json("""{"from":{"unitId":"u1","section":"A","entryKind":"word"},"to":{"unitId":"u2","section":"A","entryKind":"phrase"}}"""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([("u1", "A", "phrase"), ("u1", "A", "word"), ("u1", "B", "phrase"), ("u2", "", "phrase")], await PositionsAsync(factory));
    }

    [Theory]
    [InlineData("/admin/vocabulary-books/missing/meanings/m/positions")]
    [InlineData("/admin/vocabulary-books/B/meanings/m/positions")]
    [InlineData("/admin/vocabulary-books/A/meanings/missing/positions")]
    public async Task InvalidPathOwnership_Returns404WithoutWrites(string path)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var response = await client.PutAsync(path, Json(Move), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(4, (await PositionsAsync(factory)).Count);
    }

    [Theory]
    [InlineData("?entryKind=phrase")]
    [InlineData("?section=A")]
    [InlineData("?section=A&entryKind=")]
    [InlineData("?section=a&entryKind=phrase")]
    [InlineData("?section=A&entryKind=Word")]
    public async Task DeleteRequiresExplicitValidKey(string query)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var response = await client.DeleteAsync(Path + "/u1" + query, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(4, (await PositionsAsync(factory)).Count);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("student", 403)]
    [InlineData("legacy-cookie", 401)]
    public async Task UnauthorizedAndLegacyCookieWithoutCsrf_CannotWrite(string? identity, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        if (identity == "legacy-cookie") client.DefaultRequestHeaders.Add("Cookie", $"{VocabularyWebApplicationFactory.CookieName}={factory.CreateToken("admin")}");
        else if (identity != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(identity));
        using var move = await client.PutAsync(Path, Json(Move), TestContext.Current.CancellationToken);
        using var delete = await client.DeleteAsync(Path + "/u1?section=A&entryKind=phrase", TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)move.StatusCode);
        Assert.Equal(status, (int)delete.StatusCode);
        Assert.Equal(4, (await PositionsAsync(factory)).Count);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<List<(string UnitId, string Section, string Kind)>> PositionsAsync(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        return await db.VocabularyMeaningUnits.AsNoTracking().Where(item => item.MeaningId == "m")
            .OrderBy(item => item.UnitId).ThenBy(item => item.Section).ThenBy(item => item.EntryKind)
            .Select(item => new ValueTuple<string, string, string>(item.UnitId, item.Section, item.EntryKind))
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SeedAsync(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(
            new VocabularyBookEntity { Id = "A", BookName = "A", Status = false },
            new VocabularyBookEntity { Id = "B", BookName = "B", Status = true });
        db.Vocabularies.Add(new VocabularyEntity { Id = "w", Word = "take off" });
        db.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "m", VocabularyId = "w", BookId = "A", Meaning = "leave" });
        db.VocabularyBookUnits.AddRange(
            new VocabularyBookUnitEntity { Id = "u1", BookId = "A", Number = 1 },
            new VocabularyBookUnitEntity { Id = "u2", BookId = "A", Number = 2 },
            new VocabularyBookUnitEntity { Id = "foreign", BookId = "B", Number = 1 });
        db.VocabularyMeaningUnits.AddRange(
            new VocabularyMeaningUnitEntity { UnitId = "u1", MeaningId = "m", BookId = "A", Section = "A", EntryKind = "phrase" },
            new VocabularyMeaningUnitEntity { UnitId = "u1", MeaningId = "m", BookId = "A", Section = "A", EntryKind = "word" },
            new VocabularyMeaningUnitEntity { UnitId = "u1", MeaningId = "m", BookId = "A", Section = "B", EntryKind = "phrase" },
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "m", BookId = "A", EntryKind = "phrase" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
