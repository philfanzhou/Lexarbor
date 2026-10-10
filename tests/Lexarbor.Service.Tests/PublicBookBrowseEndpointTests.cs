using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The two anonymous book-browse routes added for callers without
/// administrator credentials: the units of an enabled book and a paged,
/// totally ordered list of its meanings.
/// </summary>
public class PublicBookBrowseEndpointTests
{
    [Fact]
    public async Task Units_EnabledBook_ListsUnitsByNumberWithCountsMatchingTheAdministrationRoute()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary-books/A/units",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        var units = body.RootElement.GetProperty("data").GetProperty("units");
        Assert.Equal(["u1", "u3", "u5"], units.EnumerateArray().Select(unit => unit.GetProperty("id").GetString()));
        Assert.All(units.EnumerateArray(), unit => Assert.Equal(
            ["id", "meaningCount", "number", "title", "wordCount"],
            unit.EnumerateObject().Select(property => property.Name).Order()));
        Assert.Equal([1, 3, 5], units.EnumerateArray().Select(unit => unit.GetProperty("number").GetInt32()));
        Assert.Equal("One", units[0].GetProperty("title").GetString());
        Assert.Equal("Three", units[1].GetProperty("title").GetString());
        Assert.Null(units[2].GetProperty("title").GetString());

        // Per distinct meaning and word: a meaning that holds Section A under
        // both entry kinds counts once, and so does one holding Section B and
        // no section.
        Assert.Equal([(2, 3), (2, 2), (0, 0)], units.EnumerateArray().Select(unit =>
            (unit.GetProperty("wordCount").GetInt32(), unit.GetProperty("meaningCount").GetInt32())));

        // The same counting the administrative unit-content route reports.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        foreach (var (unitId, expected) in new[] { ("u1", (3, 2)), ("u3", (2, 2)), ("u5", (0, 0)) })
        {
            using var admin = await client.GetAsync($"/admin/vocabulary-books/A/units/{unitId}/content",
                TestContext.Current.CancellationToken);
            admin.EnsureSuccessStatusCode();
            using var adminBody = JsonDocument.Parse(
                await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var data = adminBody.RootElement.GetProperty("data");
            Assert.Equal(expected.Item1, data.GetProperty("meaningCount").GetInt32());
            Assert.Equal(expected.Item2, data.GetProperty("wordCount").GetInt32());
        }
    }

    [Fact]
    public async Task Units_BookWithoutUnits_ReturnsEmptyArray()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary-books/E/units",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var units = body.RootElement.GetProperty("data").GetProperty("units");
        Assert.Equal(JsonValueKind.Array, units.ValueKind);
        Assert.Empty(units.EnumerateArray());
    }

    [Fact]
    public async Task Entries_WholeBook_OrdersTotallyAcrossPagesWithUnassignedLast()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        // The full order, once: unit 1's meanings by normalized spelling then
        // meaning id, then unit 3 Section A, Section B, and the meanings
        // assigned to no unit at all last.
        var expected = new[] { "m-cherry-1", "m-cherry-2", "m-date-1", "m-apple-1", "m-banana-1", "m-apple-2" };

        var paged = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            using var response = await client.GetAsync($"/api/vocabulary-books/A/entries?page={page}&size=2",
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var data = body.RootElement.GetProperty("data");
            Assert.Equal(["items", "totalCount", "totalPage", "wordCount"],
                data.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(6, data.GetProperty("totalCount").GetInt32());
            Assert.Equal(3, data.GetProperty("totalPage").GetInt32());
            Assert.Equal(4, data.GetProperty("wordCount").GetInt32());
            paged.AddRange(data.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("meaningId").GetString()!));
        }

        Assert.Equal(expected, paged);
        Assert.Equal(expected.Length, paged.Distinct().Count());

        using var whole = await client.GetAsync("/api/vocabulary-books/A/entries?page=1&size=100",
            TestContext.Current.CancellationToken);
        whole.EnsureSuccessStatusCode();
        using var wholeBody = JsonDocument.Parse(
            await whole.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, wholeBody.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("meaningId").GetString()));
    }

    [Fact]
    public async Task Entries_WholeBook_ReportsEveryPositionOfTheBookAndEmptyForUnassigned()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary-books/A/entries?page=1&size=100",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = body.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToList();

        var apple = items.Single(item => item.GetProperty("meaningId").GetString() == "m-apple-1");
        Assert.Equal(
            // Both kinds of Section A, the empty-string sentinels reading as
            // null, and the shared spelling kept as stored while its key is
            // lower(trim(...)).
            [("u3", 3, "A", "phrase"), ("u3", 3, "A", "word")],
            PositionsOf(apple));
        var banana = items.Single(item => item.GetProperty("meaningId").GetString() == "m-banana-1");
        Assert.Equal([("u3", 3, null, null), ("u3", 3, "B", null)], PositionsOf(banana));

        // The unassigned meaning is in range, sorts last, and has no place.
        var unassigned = items.Single(item => item.GetProperty("meaningId").GetString() == "m-apple-2");
        Assert.Equal("m-apple-2", items[^1].GetProperty("meaningId").GetString());
        Assert.Empty(unassigned.GetProperty("positions").EnumerateArray());
    }

    [Fact]
    public async Task Entries_UnitScope_ListsOnlyTheUnitsMeaningsAndItsPlaces()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary-books/A/entries?unitId=u3",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(["m-apple-1", "m-banana-1"],
            data.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("meaningId").GetString()));
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, data.GetProperty("wordCount").GetInt32());
        var apple = data.GetProperty("items")[0];
        Assert.Equal([("u3", 3, "A", "phrase"), ("u3", 3, "A", "word")], PositionsOf(apple));
        var banana = data.GetProperty("items")[1];
        // The other unit's places never appear in a unit-scoped page.
        Assert.Equal([("u3", 3, null, null), ("u3", 3, "B", null)], PositionsOf(banana));
    }

    [Fact]
    public async Task Entries_Keys_UseLowerTrimmedValuesForWordsAndMeanings()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/vocabulary-books/A/entries?page=1&size=100",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = body.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToList();

        var byId = items.ToDictionary(item => item.GetProperty("meaningId").GetString()!);

        // The display spelling and the definition are returned as stored,
        // while the keys fold case and trim the ends — the same
        // lower(trim(...)) rule the question generator calls equivalence.
        var cherry = byId["m-cherry-1"];
        Assert.Equal(" cherry ", cherry.GetProperty("word").GetString());
        Assert.Equal("cherry", cherry.GetProperty("normalizedWord").GetString());
        var apple = byId["m-apple-1"];
        Assert.Equal("Apple", apple.GetProperty("word").GetString());
        Assert.Equal("apple", apple.GetProperty("normalizedWord").GetString());
        Assert.Equal("Sweet FRUIT", apple.GetProperty("meaning").GetString());
        Assert.Equal("sweet fruit", apple.GetProperty("meaningKey").GetString());
        var banana = byId["m-banana-1"];
        Assert.Equal(" long yellow fruit ", banana.GetProperty("meaning").GetString());
        Assert.Equal("long yellow fruit", banana.GetProperty("meaningKey").GetString());

        // The order follows the normalized key, so the padded spelling of
        // "cherry" does not push it after "date".
        Assert.Equal("m-cherry-1", items[0].GetProperty("meaningId").GetString());
        Assert.Equal("m-date-1", items[2].GetProperty("meaningId").GetString());

        Assert.All(items, item => Assert.Equal(
            ["example", "meaning", "meaningId", "meaningKey", "normalizedWord", "partOfSpeech",
                "phoneticUk", "phoneticUs", "positions", "word", "wordId"],
            item.EnumerateObject().Select(property => property.Name).Order()));
        Assert.Equal("/æp/", byId["m-apple-1"].GetProperty("phoneticUk").GetString());
        Assert.Equal("/æp2/", byId["m-apple-1"].GetProperty("phoneticUs").GetString());
        Assert.Equal("n.", byId["m-apple-1"].GetProperty("partOfSpeech").GetString());
        Assert.Equal("An apple a day.", byId["m-apple-1"].GetProperty("example").GetString());
    }

    [Theory]
    [InlineData("/api/vocabulary-books/missing/units", 404)]
    [InlineData("/api/vocabulary-books/D/units", 422)]
    [InlineData("/api/vocabulary-books/missing/entries", 404)]
    [InlineData("/api/vocabulary-books/D/entries", 422)]
    [InlineData("/api/vocabulary-books/A/entries?unitId=nope", 404)]
    // A unit of another book answers the same 404 as a missing one.
    [InlineData("/api/vocabulary-books/A/entries?unitId=uf", 404)]
    [InlineData("/api/vocabulary-books/A/entries?page=-1", 400)]
    [InlineData("/api/vocabulary-books/A/entries?size=101", 400)]
    [InlineData("/api/vocabulary-books/A/entries?page=0&size=0", 200)]
    [InlineData("/api/vocabulary-books/A/entries?page=2147483647&size=100", 400)]
    public async Task Failures_AnswerThroughTheSharedMapping(string path, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        if (status == 200)
        {
            response.EnsureSuccessStatusCode();
        }
        else
        {
            await HttpFailureAssertions.AssertFailureAsync(response, (HttpStatusCode)status);
        }
    }

    private static List<(string UnitId, int UnitNumber, string? Section, string? EntryKind)> PositionsOf(
        JsonElement item)
    {
        return item.GetProperty("positions").EnumerateArray().Select(position => (
            position.GetProperty("unitId").GetString(),
            position.GetProperty("unitNumber").GetInt32(),
            position.GetProperty("section").GetString(),
            position.GetProperty("entryKind").GetString())).ToList()!;
    }

    /// <summary>
    /// Book A (enabled) carries unit 1 (one unsectioned place per meaning,
    /// two meanings of "cherry" and one of "Date"), unit 3 (an "Apple"
    /// meaning in Section A under both kinds, a "banana" meaning in Section B
    /// and unsectioned), and unit 5 with nothing assigned; one Apple meaning
    /// belongs to no unit. Book D is disabled, book E has no units, and book
    /// F owns unit "uf" used to prove another book's unit id answers 404.
    /// </summary>
    private static async Task SeedAsync(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.VocabularyBooks.AddRange(
            new VocabularyBookEntity { Id = "A", BookName = "A", Status = true },
            new VocabularyBookEntity { Id = "D", BookName = "D", Status = false },
            new VocabularyBookEntity { Id = "E", BookName = "E", Status = true },
            new VocabularyBookEntity { Id = "F", BookName = "F", Status = true });
        db.Vocabularies.AddRange(
            new VocabularyEntity { Id = "cherry-w", Word = " cherry " },
            new VocabularyEntity { Id = "date-w", Word = "Date" },
            new VocabularyEntity { Id = "apple-w", Word = "Apple", PhoneticUk = "/æp/", PhoneticUs = "/æp2/" },
            new VocabularyEntity { Id = "banana-w", Word = "banana" });
        db.VocabularyMeanings.AddRange(
            new VocabularyMeaningEntity
            {
                Id = "m-cherry-1",
                VocabularyId = "cherry-w",
                BookId = "A",
                Meaning = "a small red fruit"
            },
            new VocabularyMeaningEntity
            {
                Id = "m-cherry-2",
                VocabularyId = "cherry-w",
                BookId = "A",
                Meaning = "a shade of red"
            },
            new VocabularyMeaningEntity
            {
                Id = "m-date-1",
                VocabularyId = "date-w",
                BookId = "A",
                Meaning = "a calendar day"
            },
            new VocabularyMeaningEntity
            {
                Id = "m-apple-1",
                VocabularyId = "apple-w",
                BookId = "A",
                PartOfSpeech = "n.",
                Meaning = "Sweet FRUIT",
                Example = "An apple a day."
            },
            new VocabularyMeaningEntity
            {
                Id = "m-apple-2",
                VocabularyId = "apple-w",
                BookId = "A",
                Meaning = "the technology company"
            },
            new VocabularyMeaningEntity
            {
                Id = "m-banana-1",
                VocabularyId = "banana-w",
                BookId = "A",
                Meaning = " long yellow fruit "
            });
        db.VocabularyBookUnits.AddRange(
            new VocabularyBookUnitEntity { Id = "u3", BookId = "A", Number = 3, Title = "Three" },
            new VocabularyBookUnitEntity { Id = "u1", BookId = "A", Number = 1, Title = "One" },
            new VocabularyBookUnitEntity { Id = "u5", BookId = "A", Number = 5 },
            new VocabularyBookUnitEntity { Id = "ud", BookId = "D", Number = 9 },
            new VocabularyBookUnitEntity { Id = "uf", BookId = "F", Number = 1 });
        db.VocabularyMeaningUnits.AddRange(
            new VocabularyMeaningUnitEntity { UnitId = "u1", MeaningId = "m-cherry-1", BookId = "A" },
            new VocabularyMeaningUnitEntity { UnitId = "u1", MeaningId = "m-cherry-2", BookId = "A" },
            new VocabularyMeaningUnitEntity { UnitId = "u1", MeaningId = "m-date-1", BookId = "A" },
            new VocabularyMeaningUnitEntity
            {
                UnitId = "u3",
                MeaningId = "m-apple-1",
                BookId = "A",
                Section = "A",
                EntryKind = "word"
            },
            new VocabularyMeaningUnitEntity
            {
                UnitId = "u3",
                MeaningId = "m-apple-1",
                BookId = "A",
                Section = "A",
                EntryKind = "phrase"
            },
            new VocabularyMeaningUnitEntity
            {
                UnitId = "u3",
                MeaningId = "m-banana-1",
                BookId = "A",
                Section = "B"
            },
            new VocabularyMeaningUnitEntity { UnitId = "u3", MeaningId = "m-banana-1", BookId = "A" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
