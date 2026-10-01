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
    [InlineData("/admin/vocabulary-books/A/phrase-positions")]
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
    [InlineData("/admin/vocabulary-books/A/units/u2/content?section=a", 400)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?section=C", 400)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?section=all", 400)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?section=none", 200)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?section=A", 200)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?entryKind=Word", 400)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?entryKind=words", 400)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?entryKind=verb", 400)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?entryKind=word", 200)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?entryKind=phrase", 200)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?entryKind=none", 200)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?section=A&entryKind=word", 200)]
    [InlineData("/admin/vocabulary-books/A/units/u2/content?page=0&size=0", 200)]
    [InlineData("/admin/vocabulary-books/missing/phrase-positions", 404)]
    [InlineData("/admin/vocabulary-books/A/phrase-positions?unitId=ub", 404)]
    [InlineData("/admin/vocabulary-books/A/phrase-positions?section=C", 400)]
    [InlineData("/admin/vocabulary-books/A/phrase-positions?page=-1", 400)]
    [InlineData("/admin/vocabulary-books/A/phrase-positions?size=101", 400)]
    [InlineData("/admin/vocabulary-books/B/phrase-positions", 200)]
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
        if (status == 200)
        {
            Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        }
        else if (!body.RootElement.TryGetProperty("title", out _))
        {
            // Endpoint-explicit failures keep the envelope; exception-generated
            // failures are ServiceMantle Problem Details, whose shape is checked
            // by the shared failure assertions elsewhere.
            Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        }
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
        Assert.Equal(new[] { "book", "entryKindCounts", "items", "meaningCount", "sectionCounts", "totalCount", "totalPage", "unit", "wordCount" },
            unit2.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var sectionCounts = unit2.RootElement.GetProperty("sectionCounts");
        Assert.Equal(new[] { "noSection", "sectionA", "sectionB" }, sectionCounts.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(2, sectionCounts.GetProperty("noSection").GetInt32());
        Assert.Equal(0, sectionCounts.GetProperty("sectionA").GetInt32());
        Assert.Equal(0, sectionCounts.GetProperty("sectionB").GetInt32());
        var entryKindCounts = unit2.RootElement.GetProperty("entryKindCounts");
        Assert.Equal(new[] { "none", "phrase", "word" }, entryKindCounts.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(2, entryKindCounts.GetProperty("none").GetInt32());
        Assert.Equal(0, entryKindCounts.GetProperty("word").GetInt32());
        Assert.Equal(0, entryKindCounts.GetProperty("phrase").GetInt32());
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
            new[] { "entryKind", "number", "section", "title", "unitId" }, u.EnumerateObject().Select(p => p.Name).Order()));
        // The unsectioned, unclassified position serializes as null, not as the sentinels.
        Assert.All(units, u => Assert.Null(u.GetProperty("section").GetString()));
        Assert.All(units, u => Assert.Null(u.GetProperty("entryKind").GetString()));

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

    [Fact]
    public async Task PhrasePositions_HttpContractCountsPositionsAndKeepsDisabledBooksReadable()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedUnitContentAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyMeaningUnits.RemoveRange(db.VocabularyMeaningUnits);
            db.VocabularyMeaningUnits.AddRange(
                new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A", EntryKind = "phrase" },
                new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A", EntryKind = "word" },
                new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A", Section = "A", EntryKind = "phrase" },
                new VocabularyMeaningUnitEntity { UnitId = "u6", MeaningId = "a1", BookId = "A", EntryKind = "phrase" },
                new VocabularyMeaningUnitEntity { UnitId = "ub", MeaningId = "b1", BookId = "B", EntryKind = "phrase" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var first = await GetAsync(client, "/admin/vocabulary-books/A/phrase-positions?page=1&size=1");
        Assert.Equal(3, first.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, first.RootElement.GetProperty("totalPage").GetInt32());
        var item = first.RootElement.GetProperty("items")[0];
        Assert.Equal(new[] { "bookId", "entryKind", "example", "meaning", "meaningId", "number", "partOfSpeech", "phoneticUk", "phoneticUs", "section", "title", "unitId", "word", "wordId" },
            item.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("phrase", item.GetProperty("entryKind").GetString());
        Assert.Null(item.GetProperty("section").GetString());
        using var second = await GetAsync(client, "/admin/vocabulary-books/A/phrase-positions?page=2&size=1");
        Assert.Equal("A", second.RootElement.GetProperty("items")[0].GetProperty("section").GetString());
        using var narrowed = await GetAsync(client, "/admin/vocabulary-books/A/phrase-positions?unitId=u2&section=A&keyword=shared");
        Assert.Equal(1, narrowed.RootElement.GetProperty("totalCount").GetInt32());
        using var disabled = await GetAsync(client, "/admin/vocabulary-books/B/phrase-positions");
        Assert.Equal(1, disabled.RootElement.GetProperty("totalCount").GetInt32());
    }

    // The section slice of the unit-content route: the `section` query
    // parameter narrows the page, the words, and the counts to that section's
    // places, the section counts always report the whole unit, and the detail's
    // unit assignments name the place they sit in.
    [Fact]
    public async Task UnitContent_SectionQuery_NarrowsPageAndCounts_AndReportsWholeUnitSections()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedSectionedUnitContentAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));

        using var sectionA = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?section=A");
        Assert.Equal(1, sectionA.RootElement.GetProperty("wordCount").GetInt32());
        Assert.Equal(1, sectionA.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(["shared"], sectionA.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("word").GetString()));
        var counts = sectionA.RootElement.GetProperty("sectionCounts");
        Assert.Equal((1, 1, 1),
            (counts.GetProperty("sectionA").GetInt32(),
             counts.GetProperty("sectionB").GetInt32(),
             counts.GetProperty("noSection").GetInt32()));

        using var sectionB = await GetAsync(client, "/admin/vocabulary-books/B/units/ub/content?section=B");
        Assert.Equal(1, sectionB.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(["banana"], sectionB.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("word").GetString()));

        using var unsectioned = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?section=none");
        Assert.Equal(1, unsectioned.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(["solo"], unsectioned.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("word").GetString()));
        Assert.Equal((1, 1, 1),
            (unsectioned.RootElement.GetProperty("sectionCounts").GetProperty("sectionA").GetInt32(),
             unsectioned.RootElement.GetProperty("sectionCounts").GetProperty("sectionB").GetInt32(),
             unsectioned.RootElement.GetProperty("sectionCounts").GetProperty("noSection").GetInt32()));

        // Unfiltered: one meaning in A and B of one unit is one meaning, and
        // the detail reads all its places, including the other unit's.
        using var whole = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content");
        Assert.Equal(2, whole.RootElement.GetProperty("meaningCount").GetInt32());
        var sharedMeaning = whole.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("word").GetString() == "shared")
            .GetProperty("meanings").EnumerateArray().Single();
        Assert.Equal(
            [(null, 6), ("A", 2), ("B", 2)],
            sharedMeaning.GetProperty("units").EnumerateArray()
                .Select(u => (u.GetProperty("section").GetString(), u.GetProperty("number").GetInt32()))
                .OrderBy(t => t.Item1));

        using var detail = await GetAsync(client, "/admin/vocabulary/shared");
        var detailMeanings = detail.RootElement.GetProperty("meanings").EnumerateArray().ToList();
        Assert.Equal(
            [("u2", "A"), ("u2", "B"), ("u6", null)],
            detailMeanings[0].GetProperty("units").EnumerateArray()
                .Select(u => (u.GetProperty("unitId").GetString(), u.GetProperty("section").GetString()))
                .OrderBy(t => t.Item1).ThenBy(t => t.Item2 ?? ""));
    }

    // The entry-kind slice of the unit-content route: the `entryKind` query
    // parameter narrows the page, the words, and the counts to that kind's
    // places, the kind counts always report the whole unit, it combines with
    // `section` as two independent dimensions of one position, and the
    // detail's unit assignments name the kind they sit under.
    [Fact]
    public async Task UnitContent_EntryKindQuery_NarrowsPageAndCounts_AndReportsWholeUnitKinds()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        await SeedKindedUnitContentAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));

        using var words = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?entryKind=word");
        Assert.Equal(1, words.RootElement.GetProperty("wordCount").GetInt32());
        Assert.Equal(1, words.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(["shared"], words.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("word").GetString()));
        Assert.Equal((1, 2, 0), KindCounts(words.RootElement.GetProperty("entryKindCounts")));

        using var phrases = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?entryKind=phrase");
        Assert.Equal(2, phrases.RootElement.GetProperty("wordCount").GetInt32());
        Assert.Equal(2, phrases.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(["shared", "solo"], phrases.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("word").GetString()).Order());

        using var unclassified = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?entryKind=none");
        Assert.Equal(0, unclassified.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Empty(unclassified.RootElement.GetProperty("items").EnumerateArray());
        // The kind counts keep speaking for the whole unit whatever the filter.
        Assert.Equal((1, 2, 0), KindCounts(unclassified.RootElement.GetProperty("entryKindCounts")));

        // The two dimensions intersect: Section A's word place and Section B's
        // phrase places, and neither dimension alone narrows the other.
        using var sectionAWord = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?section=A&entryKind=word");
        Assert.Equal(1, sectionAWord.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(["shared"], sectionAWord.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("word").GetString()));
        using var sectionBWord = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?section=B&entryKind=word");
        Assert.Equal(0, sectionBWord.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Empty(sectionBWord.RootElement.GetProperty("items").EnumerateArray());
        using var sectionAPhrase = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content?section=A&entryKind=phrase");
        Assert.Equal(1, sectionAPhrase.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(["shared"], sectionAPhrase.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("word").GetString()));

        // Unfiltered: the same meaning under two kinds of one place counts
        // once, and the detail reads every position with its kind.
        using var whole = await GetAsync(client, "/admin/vocabulary-books/A/units/u2/content");
        Assert.Equal(2, whole.RootElement.GetProperty("meaningCount").GetInt32());
        var sharedMeaning = whole.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("word").GetString() == "shared")
            .GetProperty("meanings").EnumerateArray().Single();
        Assert.Equal(
            [("u2", "A", "phrase"), ("u2", "A", "word")],
            sharedMeaning.GetProperty("units").EnumerateArray()
                .Select(u => (u.GetProperty("unitId").GetString(), u.GetProperty("section").GetString(), u.GetProperty("entryKind").GetString()))
                .OrderBy(t => t.Item2 ?? "").ThenBy(t => t.Item3 ?? ""));

        using var detail = await GetAsync(client, "/admin/vocabulary/shared");
        var detailMeanings = detail.RootElement.GetProperty("meanings").EnumerateArray().ToList();
        Assert.Equal(
            [("u2", "A", "phrase"), ("u2", "A", "word")],
            detailMeanings[0].GetProperty("units").EnumerateArray()
                .Select(u => (u.GetProperty("unitId").GetString(), u.GetProperty("section").GetString(), u.GetProperty("entryKind").GetString()))
                .OrderBy(t => t.Item2 ?? "").ThenBy(t => t.Item3 ?? ""));
    }

    private static (int Word, int Phrase, int None) KindCounts(JsonElement entryKindCounts) => (
        entryKindCounts.GetProperty("word").GetInt32(),
        entryKindCounts.GetProperty("phrase").GetInt32(),
        entryKindCounts.GetProperty("none").GetInt32());

    /// <summary>
    /// A kind-aware variant of <see cref="SeedSectionedUnitContentAsync"/>:
    /// a1 sits in unit 2's Section A under both kinds — the same meaning under
    /// two kinds of one place — and s1 is a phrase of unit 2's Section B, so
    /// the unit holds one word place and two phrase places.
    /// </summary>
    private static async Task SeedKindedUnitContentAsync(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.Add(new VocabularyBookEntity { Id = "A", BookName = "A", Status = true });
        db.Vocabularies.AddRange(
            new VocabularyEntity { Id = "shared", Word = "shared" },
            new VocabularyEntity { Id = "solo", Word = "solo" });
        db.VocabularyMeanings.AddRange(
            new VocabularyMeaningEntity { Id = "a1", VocabularyId = "shared", BookId = "A", Meaning = "a" },
            new VocabularyMeaningEntity { Id = "s1", VocabularyId = "solo", BookId = "A", Meaning = "s" });
        db.VocabularyBookUnits.Add(new VocabularyBookUnitEntity { Id = "u2", BookId = "A", Number = 2, Title = "Two" });
        db.VocabularyMeaningUnits.AddRange(
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A", Section = "A", EntryKind = "word" },
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A", Section = "A", EntryKind = "phrase" },
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "s1", BookId = "A", Section = "B", EntryKind = "phrase" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A section-aware variant of <see cref="SeedUnitContentAsync"/>: a1 sits
    /// in unit 2's Section A and Section B both, s1 is unsectioned in unit 2,
    /// a2 stays in unit 6, and book B's single meaning is in Section B of its
    /// unit.
    /// </summary>
    private static async Task SeedSectionedUnitContentAsync(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(
            new VocabularyBookEntity { Id = "A", BookName = "A", Status = true },
            new VocabularyBookEntity { Id = "B", BookName = "B", Status = false });
        db.Vocabularies.AddRange(
            new VocabularyEntity { Id = "shared", Word = "shared" },
            new VocabularyEntity { Id = "solo", Word = "solo" },
            new VocabularyEntity { Id = "banana", Word = "banana" });
        db.VocabularyMeanings.AddRange(
            new VocabularyMeaningEntity { Id = "a1", VocabularyId = "shared", BookId = "A", Meaning = "a" },
            new VocabularyMeaningEntity { Id = "a2", VocabularyId = "shared", BookId = "A", Meaning = "b" },
            new VocabularyMeaningEntity { Id = "s1", VocabularyId = "solo", BookId = "A", Meaning = "s" },
            new VocabularyMeaningEntity { Id = "b1", VocabularyId = "banana", BookId = "B", Meaning = "c" });
        db.VocabularyBookUnits.AddRange(
            new VocabularyBookUnitEntity { Id = "u2", BookId = "A", Number = 2, Title = "Two" },
            new VocabularyBookUnitEntity { Id = "u6", BookId = "A", Number = 6 },
            new VocabularyBookUnitEntity { Id = "ub", BookId = "B", Number = 1 });
        db.VocabularyMeaningUnits.AddRange(
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A", Section = "A" },
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "a1", BookId = "A", Section = "B" },
            new VocabularyMeaningUnitEntity { UnitId = "u2", MeaningId = "s1", BookId = "A", Section = "" },
            new VocabularyMeaningUnitEntity { UnitId = "u6", MeaningId = "a1", BookId = "A", Section = "" },
            new VocabularyMeaningUnitEntity { UnitId = "u6", MeaningId = "a2", BookId = "A", Section = "" },
            new VocabularyMeaningUnitEntity { UnitId = "ub", MeaningId = "b1", BookId = "B", Section = "B" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
