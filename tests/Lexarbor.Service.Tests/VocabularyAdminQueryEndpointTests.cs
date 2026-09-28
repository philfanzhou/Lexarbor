using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public class VocabularyAdminQueryEndpointTests
{
    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "maintainer")]
    [InlineData(true, "maintainer")]
    public async Task AllThreeReads_UseRealAuthorizationAndExactManagementContracts(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = factory.CreateClient();
        var token = factory.CreateToken(role);
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", $"{VocabularyWebApplicationFactory.CookieName}={token}");
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBooks.Add(new VocabularyBookEntity { Id = "disabled", BookName = "Disabled", Status = false });
            db.Vocabularies.AddRange(new VocabularyEntity { Id = "w", Word = "word" }, new VocabularyEntity { Id = "h", Word = "historical" });
            db.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "m", VocabularyId = "w", BookId = "disabled", Meaning = "definition" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var all = await GetAsync(client, "/admin/vocabulary");
        Assert.Equal(2, all.RootElement.GetProperty("totalCount").GetInt32());
        var items = all.RootElement.GetProperty("items");
        Assert.Equal(new[] { "books", "id", "phoneticUk", "phoneticUs", "word" }, items[0].EnumerateObject().Select(p => p.Name).Order());
        Assert.Empty(items[0].GetProperty("books").EnumerateArray());
        using var detail = await GetAsync(client, "/admin/vocabulary/w");
        Assert.False(detail.RootElement.GetProperty("books")[0].GetProperty("status").GetBoolean());
        var meaning = detail.RootElement.GetProperty("meanings")[0];
        Assert.Equal(new[] { "bookId", "example", "id", "meaning", "partOfSpeech", "units", "vocabularyId" }, meaning.EnumerateObject().Select(p => p.Name).Order());
        Assert.Empty(meaning.GetProperty("units").EnumerateArray());
        using var content = await GetAsync(client, "/admin/vocabulary-books/disabled/content");
        Assert.Equal(new[] { "book", "items", "meaningCount", "totalCount", "totalPage", "wordCount" }, content.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(1, content.RootElement.GetProperty("meaningCount").GetInt32());
        using var publicResult = await GetAsync(client, "/api/vocabulary?keyword=word");
        Assert.Empty(publicResult.RootElement.GetProperty("items").EnumerateArray());
        using var publicDetail = await client.GetAsync("/api/vocabulary/w?bookId=disabled", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publicDetail.StatusCode);
        using var oldWords = await GetAsync(client, "/admin/vocabulary-books/disabled/words");
        Assert.False(oldWords.RootElement.GetProperty("items")[0].TryGetProperty("books", out _));
    }

    [Theory]
    [InlineData("/admin/vocabulary")]
    [InlineData("/admin/vocabulary/w")]
    [InlineData("/admin/vocabulary-books/A/content")]
    [InlineData("/admin/vocabulary-books/A/units/u/content")]
    public async Task AnonymousAndNonAdmin_ReadsAreRejectedBeforeResourceAccess(string path)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        foreach (var role in new[] { "", "student" })
        {
            if (role.Length > 0) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(role));
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(role.Length == 0 ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new[] { "message", "success" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        }
    }

    [Theory]
    [InlineData("/admin/vocabulary?page=-1", 400)]
    [InlineData("/admin/vocabulary?size=101", 400)]
    [InlineData("/admin/vocabulary?size=-1", 400)]
    [InlineData("/admin/vocabulary?page=2147483648", 400)]
    [InlineData("/admin/vocabulary?page=2147483647&size=100", 400)]
    [InlineData("/admin/vocabulary?bookId=missing", 404)]
    [InlineData("/admin/vocabulary/missing", 404)]
    [InlineData("/admin/vocabulary-books/missing/content", 404)]
    [InlineData("/admin/vocabulary-books/missing/content?size=101", 400)]
    [InlineData("/admin/vocabulary-books/A/content?size=101", 400)]
    [InlineData("/admin/vocabulary-books/missing/units/u/content", 404)]
    [InlineData("/admin/vocabulary-books/A/units/missing/content", 404)]
    [InlineData("/admin/vocabulary-books/A/units/u/content?size=101", 400)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?page=0&size=0", 200)]
    [InlineData("/admin/vocabulary?size=100", 200)]
    [InlineData("/admin/vocabulary?page=0&size=0", 200)]
    public async Task ValidationAndMissingResources_KeepFailureEnvelope(string path, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedUnitContentAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(status == 200, body.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task UnitContent_RespondsWithUnitScopeMeaningsAndDetailUnits()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedUnitContentAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));

        // Cross-book unit id: same 404 as a missing one, through the envelope.
        using var crossBook = await client.GetAsync(
            "/admin/vocabulary-books/B/units/u2/content", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, crossBook.StatusCode);

        using var unit2 = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content");
        Assert.Equal(new[] { "book", "items", "meaningCount", "totalCount", "totalPage", "unit", "wordCount" },
            unit2.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var unit = unit2.RootElement.GetProperty("unit");
        Assert.Equal(new[] { "bookId", "id", "number", "title" }, unit.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("u2", unit.GetProperty("id").GetString());
        Assert.Equal("A", unit.GetProperty("bookId").GetString());
        Assert.Equal(2, unit.GetProperty("number").GetInt32());
        Assert.Equal("Two", unit.GetProperty("title").GetString());
        Assert.Equal(2, unit2.RootElement.GetProperty("wordCount").GetInt32());
        Assert.Equal(2, unit2.RootElement.GetProperty("meaningCount").GetInt32());
        var shared = unit2.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("word").GetString() == "shared");
        var unitMeanings = shared.GetProperty("meanings").EnumerateArray().ToList();
        Assert.Equal("a1", Assert.Single(unitMeanings).GetProperty("id").GetString());
        var units = Assert.Single(unitMeanings).GetProperty("units").EnumerateArray().ToList();
        Assert.Equal([2, 6], units.Select(u => u.GetProperty("number").GetInt32()));
        Assert.All(units, u => Assert.Equal(
            new[] { "number", "title", "unitId" }, u.EnumerateObject().Select(p => p.Name).Order()));

        // A keyword narrows the page but not the unit totals.
        using var narrowed = await GetAsync(client, "/admin/vocabulary-books/A/units/u6/content?keyword=solo");
        Assert.Equal(1, narrowed.RootElement.GetProperty("wordCount").GetInt32());
        Assert.Equal(2, narrowed.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(0, narrowed.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Empty(narrowed.RootElement.GetProperty("items").EnumerateArray());

        // A disabled book stays readable on the unit route as on the book route.
        using var disabled = await GetAsync(client, "/admin/vocabulary-books/B/units/ub/content");
        Assert.Equal(1, disabled.RootElement.GetProperty("meaningCount").GetInt32());

        // The word detail carries each meaning's unit assignments.
        using var detail = await GetAsync(client, "/admin/vocabulary/shared");
        var detailMeanings = detail.RootElement.GetProperty("meanings").EnumerateArray().ToList();
        Assert.Equal(["a1", "a2", "b1"], detailMeanings.Select(m => m.GetProperty("id").GetString()));
        Assert.Equal([2, 6], detailMeanings[0].GetProperty("units").EnumerateArray()
            .Select(u => u.GetProperty("number").GetInt32()).ToList());
        // The other books' assignments appear too: b1 belongs to book B's unit.
        var otherBookUnits = detailMeanings[2].GetProperty("units").EnumerateArray().ToList();
        Assert.Equal("ub", Assert.Single(otherBookUnits).GetProperty("unitId").GetString());

        // The public detail is untouched by the administrative extension.
        using var publicDetail = await GetAsync(client, "/api/vocabulary/shared?bookId=A");
        var publicMeaning = publicDetail.RootElement.GetProperty("meanings")[0];
        Assert.Equal(new[] { "bookId", "example", "id", "meaning", "partOfSpeech", "vocabularyId" },
            publicMeaning.EnumerateObject().Select(p => p.Name).Order());

        // Whole-book content counts are unchanged by unit assignments.
        using var bookContent = await GetAsync(client, "/admin/vocabulary-books/A/content");
        Assert.Equal(4, bookContent.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(3, bookContent.RootElement.GetProperty("wordCount").GetInt32());
    }

    private static async Task SeedUnitContentAsync(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(
            new VocabularyBookEntity { Id = "A", BookName = "A", Status = true },
            new VocabularyBookEntity { Id = "B", BookName = "B", Status = false });
        db.Vocabularies.AddRange(
            new VocabularyEntity { Id = "shared", Word = "shared" },
            new VocabularyEntity { Id = "solo", Word = "solo" },
            new VocabularyEntity { Id = "unassigned", Word = "unassigned" });
        db.VocabularyMeanings.AddRange(
            new VocabularyMeaningEntity { Id = "a1", VocabularyId = "shared", BookId = "A", Meaning = "a" },
            new VocabularyMeaningEntity { Id = "a2", VocabularyId = "shared", BookId = "A", Meaning = "b" },
            new VocabularyMeaningEntity { Id = "s1", VocabularyId = "solo", BookId = "A", Meaning = "s" },
            new VocabularyMeaningEntity { Id = "x1", VocabularyId = "unassigned", BookId = "A", Meaning = "x" },
            new VocabularyMeaningEntity { Id = "b1", VocabularyId = "shared", BookId = "B", Meaning = "c" });
        db.VocabularyBookUnits.AddRange(
            new VocabularyBookUnitEntity { Id = "u2", BookId = "A", Number = 2, Title = "Two" },
            new VocabularyBookUnitEntity { Id = "u6", BookId = "A", Number = 6 },
            new VocabularyBookUnitEntity { Id = "ub", BookId = "B", Number = 1 });
        db.VocabularyMeaningUnits.AddRange(
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A" },
            new VocabularyMeaningUnitEntity { UnitId = "u6", MeaningId = "a1", BookId = "A" },
            new VocabularyMeaningUnitEntity { UnitId = "u6", MeaningId = "a2", BookId = "A" },
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "s1", BookId = "A" },
            new VocabularyMeaningUnitEntity { UnitId = "ub", MeaningId = "b1", BookId = "B" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return JsonDocument.Parse(body.RootElement.GetProperty("data").GetRawText());
    }
}
