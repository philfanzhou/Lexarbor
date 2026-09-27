using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public class VocabularyCleanupEndpointTests
{
    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "maintainer")]
    [InlineData(true, "maintainer")]
    public async Task PreviewAndCommit_UseCookieBearerAndCustomRole(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration:
            new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = factory.CreateClient(); await SeedAsync(factory.Services);
        Authenticate(client, factory, cookie, role); client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var before = await StateAsync(factory.Services);
        using var preview = await PostAsync(client, """{"action":"clear"}""", true);
        using var previewBody = await DataAsync(preview);
        Assert.Equal(new[] { "action", "affectedWordCount", "bookId", "bookName", "meaningCount", "orphanWordCount" }, previewBody.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(3, previewBody.RootElement.GetProperty("meaningCount").GetInt32());
        Assert.Equal(before, await StateAsync(factory.Services));
        using var commit = await PostAsync(client, """{"action":"clear"}""");
        using var committed = await DataAsync(commit);
        Assert.Equal(new[] { "action", "affectedWordCount", "bookId", "deletedBook", "deletedMeaningCount", "deletedWordCount" }, committed.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(3, committed.RootElement.GetProperty("deletedMeaningCount").GetInt32());
        Assert.Equal(1, committed.RootElement.GetProperty("deletedWordCount").GetInt32());
        Assert.False(committed.RootElement.GetProperty("deletedBook").GetBoolean());
    }

    [Theory]
    [InlineData("{\"action\":\"removeMeaning\",\"wordId\":\"shared\",\"meaningId\":\"a1\"}", 1, 1, 0, false)]
    [InlineData("{\"action\":\"removeWords\",\"wordIds\":[\"shared\",\"only-a\",\"shared\"]}", 2, 3, 1, false)]
    [InlineData("{\"action\":\"clear\"}", 2, 3, 1, false)]
    [InlineData("{\"action\":\"delete\",\"confirmedBookName\":\"A\"}", 2, 3, 1, true)]
    public async Task EachAction_ReturnsActualDeduplicatedCounts(string body, int words, int meanings, int orphans, bool deletedBook)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient(); await SeedAsync(factory.Services); Authenticate(client, factory, false, "admin");
        using var preview = await PostAsync(client, body, true); using var p = await DataAsync(preview);
        Assert.Equal((words, meanings, orphans), (p.RootElement.GetProperty("affectedWordCount").GetInt32(), p.RootElement.GetProperty("meaningCount").GetInt32(), p.RootElement.GetProperty("orphanWordCount").GetInt32()));
        using var commit = await PostAsync(client, body); using var c = await DataAsync(commit);
        Assert.Equal((words, meanings, orphans, deletedBook), (c.RootElement.GetProperty("affectedWordCount").GetInt32(), c.RootElement.GetProperty("deletedMeaningCount").GetInt32(), c.RootElement.GetProperty("deletedWordCount").GetInt32(), c.RootElement.GetProperty("deletedBook").GetBoolean()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"action\":false}")]
    [InlineData("{\"action\":\"unknown\"}")]
    [InlineData("{\"action\":\"clear\",\"wordId\":null}")]
    [InlineData("{\"action\":\"clear\",\"wordIds\":[]}")]
    [InlineData("{\"action\":\"clear\",\"confirmedBookName\":\"A\"}")]
    [InlineData("{\"action\":\"clear\",\"surprise\":1}")]
    [InlineData("{\"action\":\"clear\",\"action\":\"delete\"}")]
    [InlineData("{\"action\":\"removeMeaning\",\"wordId\":\"shared\"}")]
    [InlineData("{\"action\":\"removeMeaning\",\"wordId\":\" \",\"meaningId\":\"a1\"}")]
    [InlineData("{\"action\":\"removeMeaning\",\"wordId\":1,\"meaningId\":\"a1\"}")]
    [InlineData("{\"action\":\"removeWords\",\"wordIds\":[]}")]
    [InlineData("{\"action\":\"removeWords\",\"wordIds\":[null]}")]
    [InlineData("{\"action\":\"removeWords\",\"wordIds\":[\"\"]}")]
    [InlineData("{\"action\":\"removeWords\",\"wordIds\":\"shared\"}")]
    [InlineData("{\"action\":\"delete\",\"confirmedBookName\":null}")]
    public async Task InvalidShapes_RejectBothEndpointsWithoutWriting(string body)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient(); await SeedAsync(factory.Services); Authenticate(client, factory, false, "admin");
        var before = await StateAsync(factory.Services);
        foreach (var preview in new[] { false, true }) { using var response = await PostAsync(client, body, preview); await FailureAsync(response, 400); }
        Assert.Equal(before, await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie-no-csrf", 403)]
    public async Task AuthorizationAndCsrf_PrecedeParsingAndResourceChecks(string identity, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient(); await SeedAsync(factory.Services);
        if (identity != "anonymous") Authenticate(client, factory, identity == "cookie-no-csrf", identity == "student" ? "student" : "admin");
        var before = await StateAsync(factory.Services);
        foreach (var preview in new[] { false, true }) { using var response = await PostAsync(client, "{", preview); await FailureAsync(response, status); }
        Assert.Equal(before, await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ByteLimit_CoversContentLengthAndChunkedBodies(bool chunked)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient(); await SeedAsync(factory.Services); Authenticate(client, factory, false, "admin");
        var before = await StateAsync(factory.Services);
        var body = "{\"action\":\"clear\"}".PadRight(VocabularyCleanupEndpoints.MaxRequestBytes + 1);
        foreach (var preview in new[] { false, true })
        {
            using var content = chunked ? (HttpContent)new ChunkedContent(body) : new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(Path(preview), content, TestContext.Current.CancellationToken);
            await FailureAsync(response, 413);
        }
        using var exact = await PostAsync(client, body[..^1], true); exact.EnsureSuccessStatusCode();
        Assert.Equal(before, await StateAsync(factory.Services));
    }

    [Fact]
    public async Task MissingOwnershipSelectionAndNameFailures_AreAtomic()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient(); await SeedAsync(factory.Services); Authenticate(client, factory, false, "admin");
        var before = await StateAsync(factory.Services);
        foreach (var (body, status) in new[]
        {
            ("""{"action":"removeMeaning","wordId":"missing","meaningId":"a1"}""",404),
            ("""{"action":"removeMeaning","wordId":"shared","meaningId":"missing"}""",404),
            ("""{"action":"removeMeaning","wordId":"only-a","meaningId":"a1"}""",409),
            ("""{"action":"removeMeaning","wordId":"shared","meaningId":"b1"}""",409),
            ("""{"action":"removeWords","wordIds":["shared","missing"]}""",409),
            ("""{"action":"delete","confirmedBookName":"a"}""",409),
            ("""{"action":"delete"}""",400),
            (JsonSerializer.Serialize(new { action = "removeWords", wordIds = Enumerable.Repeat("shared", 101) }),400)
        }) { using var response = await PostAsync(client, body); await FailureAsync(response, status); }
        using var missing = await client.PostAsync("/admin/vocabulary-books/missing/cleanup", new StringContent("""{"action":"clear"}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        await FailureAsync(missing, 404);
        using var legacy = await client.DeleteAsync("/admin/vocabulary-books/A", TestContext.Current.CancellationToken); await FailureAsync(legacy, 409);
        using var deletePreview = await PostAsync(client, """{"action":"delete"}""", true); deletePreview.EnsureSuccessStatusCode();
        Assert.Equal(before, await StateAsync(factory.Services));
    }

    [Fact]
    public async Task ConstraintFailure_Reports409AndRestoresAllData()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient(); await SeedAsync(factory.Services); Authenticate(client, factory, false, "admin");
        var before = await StateAsync(factory.Services);
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_delete BEFORE DELETE ON vocabulary BEGIN SELECT RAISE(ABORT,'synthetic'); END;", TestContext.Current.CancellationToken);
        using var response = await PostAsync(client, """{"action":"clear"}"""); await FailureAsync(response, 409);
        Assert.Equal(before, await StateAsync(factory.Services));
    }

    [Fact]
    public async Task ExternalSqliteWriter_Returns503AndRetryAfterWithoutWrites()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lexarbor-cleanup-http-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = new VocabularyWebApplicationFactory();
            await using var fileFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<VocabularyDbContext>>(); services.RemoveAll<VocabularyDbContext>();
                services.AddDbContext<VocabularyDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False;Default Timeout=1"));
            }));
            using var client = fileFactory.CreateClient(); await SeedAsync(fileFactory.Services); Authenticate(client, factory, false, "admin");
            var before = await StateAsync(fileFactory.Services);
            using var scope = fileFactory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                using var response = await PostAsync(client, """{"action":"clear"}"""); await FailureAsync(response, 503);
                Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
            }
            Assert.Equal(before, await StateAsync(fileFactory.Services));
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private static string Path(bool preview) => "/admin/vocabulary-books/A/cleanup" + (preview ? "/preview" : "");
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body, bool preview = false) => client.PostAsync(Path(preview), new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
    private static void Authenticate(HttpClient client, VocabularyWebApplicationFactory factory, bool cookie, string role)
    {
        var token = factory.CreateToken(role);
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", $"{VocabularyWebApplicationFactory.CookieName}={token}");
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    private static async Task<JsonDocument> DataAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return JsonDocument.Parse(body.RootElement.GetProperty("data").GetRawText());
    }
    private static async Task FailureAsync(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "message", "success" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
    }
    private static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = true }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = false });
        db.Vocabularies.AddRange(new[] { "shared", "only-a", "historical" }.Select(v => new VocabularyEntity { Id = v, Word = v }));
        db.VocabularyMeanings.AddRange(new[] { ("a1", "shared", "A"), ("a2", "shared", "A"), ("a-only", "only-a", "A"), ("b1", "shared", "B") }
            .Select(m => new VocabularyMeaningEntity { Id = m.Item1, VocabularyId = m.Item2, BookId = m.Item3, Meaning = m.Item1 }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    private static async Task<string> StateAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        return JsonSerializer.Serialize(new
        {
            Books = await db.VocabularyBooks.AsNoTracking().OrderBy(b => b.Id).ToListAsync(TestContext.Current.CancellationToken),
            Words = await db.Vocabularies.AsNoTracking().OrderBy(v => v.Id).ToListAsync(TestContext.Current.CancellationToken),
            Meanings = await db.VocabularyMeanings.AsNoTracking().OrderBy(m => m.Id).ToListAsync(TestContext.Current.CancellationToken)
        });
    }
    private sealed class ChunkedContent : HttpContent
    {
        private readonly string _body;
        public ChunkedContent(string body) { _body = body; Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { await stream.WriteAsync(Encoding.UTF8.GetBytes(_body)); }
    }
}
