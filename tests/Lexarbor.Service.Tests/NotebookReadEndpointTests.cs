using System.Net;
using System.Text.Json;
using Lexarbor.Learning.Database;
using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class NotebookReadEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    // Exactly midnight in Beijing on November 25. November 19 starts at 16:00 UTC on November 18.
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 16, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static string Root(Guid student) => $"/learning/v1/api/vocabulary/students/{student}/notebook";

    [Fact]
    public async Task LargeNotebookCountsAndPagesMatchTheStoredAndRefreshedReviews()
    {
        var catalog = new Mock<IVocabularyNotebookCatalog>(MockBehavior.Strict);
        var lookup = catalog.Setup(service => service.LookupAsync(It.Is<IReadOnlyCollection<string>>(ids =>
            ids.Count == 1 && ids.Contains("review-0")), It.IsAny<CancellationToken>()));
        lookup.ThrowsAsync(new VocabCatalogUnavailableException());
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(new Clock());
            services.AddSingleton(catalog.Object);
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("notebook", catalog: false, learning: true));
        var student = Guid.NewGuid();
        var root = Root(student);
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        var storage = host.Services.GetRequiredService<LearningStorage>();
        await using var db = new LearningDbContext(storage.Options("notebook"));
        var items = await SeedNotebookAsync(db, student);

        async Task<VocabNotebookSummary?> Summary() => (await Data(await client.GetAsync(root + "/summary", Ct)))
            .Deserialize<VocabNotebookSummary>(JsonSerializerOptions.Web);
        async Task<JsonElement> Page(string query) => await Data(await client.GetAsync(root + "/query?" + query, Ct));

        async Task<CardKey[]> Membership(params CardKey[] keys) =>
            (await Data(await client.PostAsJsonAsync(root + "/membership", new { keys }, Ct)))
            .GetProperty("included").Deserialize<CardKey[]>(JsonSerializerOptions.Web)!;

        Assert.Equal(Key(items[0]), Assert.Single(await Membership(Key(items[0]))));
        catalog.VerifyNoOtherCalls();
        Assert.Equal(new(1000, 1048, 182, 24, 2), await Summary());
        lookup.ReturnsAsync([new("review-0", VocabSubmissionStatus.Rejected, "Word0", null, "meaning0", "word0", "meaning0", true,
            null, null, null, null, null, null, null, null, "Try again")]);
        Assert.Equal(new(1000, 1048, 182, 24, 3), await Summary());

        var intersection = await Page("keyword=%20wOrD%20&teacher=true&recent=true&rejected=true");
        Assert.Equal(2, intersection.GetProperty("total").GetInt32());
        Assert.Equal("Try again", intersection.GetProperty("items")[0].GetProperty("reviewComment").GetString());
        Assert.Equal(182, (await Page("teacher=true")).GetProperty("total").GetInt32());
        Assert.Equal(3, (await Page("rejected=true")).GetProperty("total").GetInt32());
        var first = await Page("recent=true");
        var last = await Page("recent=true&page=999999999999");
        Assert.Equal(24, first.GetProperty("total").GetInt32());
        Assert.Equal(20, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, last.GetProperty("page").GetInt32());
        Assert.Equal(items.Take(24).Select(item => item.Id), first.GetProperty("items").EnumerateArray()
            .Concat(last.GetProperty("items").EnumerateArray()).Select(item => item.GetProperty("id").GetGuid()));

        var special = Assert.Single((await Page("keyword=" + Uri.EscapeDataString("观察%_^"))).GetProperty("items").EnumerateArray());
        Assert.Equal(items[2].Id, special.GetProperty("id").GetGuid());
        Assert.True(special.GetProperty("learned").GetBoolean());
        Assert.Equal("Original", special.GetProperty("original").GetProperty("word").GetString());
        Assert.Equal("Revised", special.GetProperty("reviewComment").GetString());
        Assert.Equal("derive", special.GetProperty("familySource").GetProperty("word").GetString());
        Assert.Single(special.GetProperty("familySources").EnumerateArray());
        Assert.Equal(2, (await Page("keyword=" + Uri.EscapeDataString("观察"))).GetProperty("total").GetInt32());
        var absent = await Page("keyword=absent&page=900");
        Assert.Equal(0, absent.GetProperty("total").GetInt32());
        Assert.Equal(1, absent.GetProperty("page").GetInt32());
        Assert.Empty(absent.GetProperty("items").EnumerateArray());

        var included = await Membership(Key(items[2]), Key(items[2]), Key(items[0]), new("word2", items[3].MeaningKey),
            new("word3", items[2].MeaningKey), new("Word2", items[2].MeaningKey), new(" word2", items[2].MeaningKey));
        Assert.Equal(Key(items[2]), Assert.Single(included));
        catalog.Setup(service => service.SearchAsync("word0", It.IsAny<CancellationToken>())).ReturnsAsync([
            new("word0", "WORD0", "word0", null, null, "book", "Book", "approved", null, "meaning0", "meaning0")]);
        await Data(await client.PostAsJsonAsync(root + "/items", new { actor = new { type = "student" }, word = "word0", meaningId = "approved" }, Ct));
        Assert.Equal(Key(items[0]), Assert.Single(await Membership(Key(items[0]))));
        Assert.Equal(new(1000, 1048, 182, 24, 2), await Summary());

        var otherStudent = Guid.NewGuid();
        (await client.PutAsync($"/learning/v1/learners/{otherStudent}", null, Ct)).EnsureSuccessStatusCode();
        await AssertEmptyAsync(client, otherStudent, Key(items[2]));
        using var other = host.CreateClient();
        other.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("another", catalog: false, learning: true));
        (await other.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        await AssertEmptyAsync(other, student, Key(items[2]));
    }

    private static async Task<VocabNotebookItemEntity[]> SeedNotebookAsync(LearningDbContext db, Guid student)
    {
        var teacher = Guid.NewGuid();
        var boundary = Now.AddDays(-6);
        var rows = Enumerable.Range(0, 1048).Select(i =>
        {
            var word = $"word{i % 1000}";
            var meaning = i switch { 2 => "观察%_^", 3 => "观察xyz", _ => $"meaning{i}" };
            var reviewed = i is 0 or 1 or 30;
            return new VocabNotebookItemEntity
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}"),
                StudentId = student,
                WordKey = word,
                MeaningKey = meaning,
                Word = word.ToUpperInvariant(),
                Meaning = meaning,
                Status = (short)(i == 0 ? VocabNotebookItemStatus.Pending : reviewed ? VocabNotebookItemStatus.Rejected : VocabNotebookItemStatus.Ready),
                BookId = reviewed ? null : "book",
                WordId = reviewed ? null : word,
                MeaningId = reviewed ? null : meaning,
                SubmissionId = reviewed ? $"review-{i}" : null,
                AddedBy = (short)(i < 182 ? 2 : 1),
                AddedByTeacherId = i < 182 ? teacher : null,
                AddedAt = i < 24 ? boundary : boundary.AddMicroseconds(-1),
                UpdatedAt = Now
            };
        }).ToArray();
        rows[2].OriginalWord = "Original";
        rows[2].OriginalMeaning = "original meaning";
        rows[2].ReviewComment = "Revised";
        rows[2].FamilySources.Add(new()
        {
            Id = Guid.NewGuid(),
            SourceWord = "derive",
            SourceWordId = "derive",
            SourceWordKey = "derive",
            Direction = "outgoing",
            RelationType = "derived_noun",
            RelationId = "relation",
            SourceMeaningId = "source",
            SourceBookId = "book",
            TargetWordId = rows[2].WordId!,
            TargetMeaningId = rows[2].MeaningId!,
            TargetBookId = "book"
        });
        db.VocabNotebookItems.AddRange(rows);
        db.MemoryCards.Add(new()
        {
            StudentId = student,
            WordKey = rows[2].WordKey,
            MeaningKey = rows[2].MeaningKey,
            State = 2,
            DueAt = Now,
            LastReviewedAt = Now,
            FirstLearnedAt = Now
        });
        await db.SaveChangesAsync(Ct);
        return rows;
    }

    private static async Task AssertEmptyAsync(HttpClient client, Guid student, CardKey candidate)
    {
        var root = Root(student);
        var summary = await Data(await client.GetAsync(root + "/summary", Ct));
        Assert.Equal(new(0, 0, 0, 0, 0), summary.Deserialize<VocabNotebookSummary>(JsonSerializerOptions.Web));
        Assert.Empty((await Data(await client.GetAsync(root + "/query?teacher=true", Ct))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Data(await client.PostAsJsonAsync(root + "/membership", new { keys = new[] { candidate } }, Ct)))
            .GetProperty("included").EnumerateArray());
    }

    [Fact]
    public async Task ValidatesInputsAndChecksScopeStoreAndGenerationOnAllThreeRoutes()
    {
        await using var host = new VocabularyWebApplicationFactory();
        using var client = host.CreateClient();
        var student = Guid.NewGuid();
        var root = Root(student);
        async Task CheckRoutes(HttpStatusCode expected)
        {
            Assert.Equal(expected, (await client.GetAsync(root + "/summary", Ct)).StatusCode);
            Assert.Equal(expected, (await client.GetAsync(root + "/query?recent=true", Ct)).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync(root + "/membership", new { keys = Array.Empty<CardKey>() }, Ct)).StatusCode);
        }

        await client.UseCatalogKeyAsync(host);
        await CheckRoutes(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("validate", catalog: false, learning: true));
        await CheckRoutes(HttpStatusCode.NotFound);
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        await CheckRoutes(HttpStatusCode.OK);
        foreach (var invalid in new[] { "", "keyword=%20&teacher=false", "recent=1", "teacher=", "rejected=yes", "recent=true&page=0",
            "recent=true&page=-1", "recent=true&page=1.5", "recent=true&page=", "keyword=" + new string('a', 101) })
            await AssertInvalid(await client.GetAsync(root + "/query?" + invalid, Ct), "VOCAB_NOTEBOOK_QUERY_INVALID");
        var pair = new CardKey("word", "meaning");
        foreach (var body in new object[] { new { }, new { keys = new object?[] { null } }, new { keys = new[] { new { wordKey = "word" } } },
            new { keys = new[] { new CardKey("  ", "m") } }, new { keys = Enumerable.Repeat(pair, 101) } })
            await AssertInvalid(await client.PostAsJsonAsync(root + "/membership", body, Ct), "VOCAB_NOTEBOOK_MEMBERSHIP_INVALID");
        Assert.Empty((await Data(await client.PostAsJsonAsync(root + "/membership", new { keys = Enumerable.Repeat(pair, 100) }, Ct)))
            .GetProperty("included").EnumerateArray());

        client.DefaultRequestHeaders.Add("X-Learning-Store", Guid.NewGuid().ToString());
        await CheckRoutes(HttpStatusCode.Conflict);
        client.DefaultRequestHeaders.Remove("X-Learning-Store");
        var generation = Guid.NewGuid();
        await using var db = new LearningDbContext(host.Services.GetRequiredService<LearningStorage>().Options("validate"));
        await db.Database.ExecuteSqlAsync($"UPDATE learning_schema_version SET generation={generation}", Ct);
        await CheckRoutes(HttpStatusCode.Conflict);
        client.DefaultRequestHeaders.Add("X-Learning-Generation", generation.ToString());
        await CheckRoutes(HttpStatusCode.OK);
    }

    private static async Task AssertInvalid(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("data");
    }

    private static CardKey Key(VocabNotebookItemEntity item) => new(item.WordKey, item.MeaningKey);
}
