using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class VocabularyRelationEndpointTests
{
    private const string Route = "/admin/vocabulary-relations";
    private const string Create = """{"sourceWordId":"write","targetWordId":"wrote","type":"past_tense"}""";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(true, "maintainer")]
    public async Task FullLifecycleUsesAdminAuthenticationAndVersionedMaintenance(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = await Client(factory, role, cookie);
        using var types = await client.GetAsync("/admin/vocabulary-relation-types", Ct);
        Assert.Equal(11, (await Data(types)).GetArrayLength());
        using var created = await client.PostAsync(Route, Json(Create), Ct);
        var original = await Data(created);
        var id = original.GetProperty("id").GetString()!;
        Assert.Equal(13, original.EnumerateObject().Count());
        Assert.Equal("manual", original.GetProperty("originKind").GetString());
        Assert.Equal("needs_review", original.GetProperty("status").GetString());
        using var incoming = await client.GetAsync("/admin/vocabulary/wrote/relations?direction=incoming", Ct);
        Assert.Equal(id, (await Data(incoming)).GetProperty("items")[0].GetProperty("id").GetString());
        using var replay = await client.PostAsync(Route, Json(Create), Ct);
        Assert.Equal(original.GetRawText(), (await Data(replay)).GetRawText());
        var replacement = new { sourceWordId = "write", targetWordId = "wrote", type = "past_tense", usageNote = (string?)null, confirm = true, expectedVersion = 1 };
        using var confirmed = await client.PutAsJsonAsync(Route + "/" + id, replacement, Ct);
        var current = await Data(confirmed);
        Assert.Equal("confirmed", current.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("reviewReason").ValueKind);
        Assert.Equal(2, current.GetProperty("version").GetInt64());
        using var stale = await client.PutAsJsonAsync(Route + "/" + id, replacement, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var queue = await client.GetAsync(Route + "?status=needs_review", Ct);
        Assert.Equal(0, (await Data(queue)).GetProperty("totalCount").GetInt32());
        using var deleted = await client.DeleteAsync(Route + "/" + id + "?expectedVersion=2", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var again = await client.DeleteAsync(Route + "/" + id + "?expectedVersion=2", Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var missing = await client.GetAsync(Route + "/" + id, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie-no-csrf", 401)]
    public async Task UnauthorizedWritesCannotReachParsingOrMutation(string identity, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await Seed(factory);
        if (identity == "cookie-no-csrf") client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie());
        else if (identity == "student") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("student"));
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Route : Route + "/missing?expectedVersion=1") { Content = Json("{") };
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(status, (int)response.StatusCode);
        }
        using var batch = await client.PostAsync(Route + "/batch", Json("{"), Ct);
        Assert.Equal(status, (int)batch.StatusCode);
        if (identity != "cookie-no-csrf")
            foreach (var route in new[] { Route, Route + "/missing", "/admin/vocabulary-relation-types", "/admin/vocabulary/write/relations" })
            {
                using var response = await client.GetAsync(route, Ct);
                Assert.Equal(status, (int)response.StatusCode);
            }
        Assert.Equal(0, await Count(factory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("{\"sourceWordId\":null,\"targetWordId\":\"wrote\",\"type\":\"past_tense\"}")]
    [InlineData("{\"sourceWordId\":\"write\",\"targetWordId\":\"wrote\",\"type\":\"past_tense\",\"type\":\"plural\"}")]
    [InlineData("{\"sourceWordId\":\"write\",\"targetWordId\":\"wrote\",\"type\":\"past_tense\",\"originKind\":\"generated\"}")]
    [InlineData("{\"sourceWordId\":\"write\",\"targetWordId\":\"wrote\",\"type\":\"past_tense\",\"sourcePartOfSpeech\":\"verb\"}")]
    public async Task InvalidBodiesRejectUnknownAndRemovedFields(string body)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = await Client(factory);
        using var response = await client.PostAsync(Route, Json(body), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Count(factory));
    }

    [Fact]
    public async Task BatchCreationIsAtomicAndReportsReuseWithServerOwnedProvenance()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = await Client(factory);
        using var invalid = await client.PostAsync(Route + "/batch", Json("{\"items\":[" + Create + "," + Create.Replace("wrote", "missing") + "]}"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        Assert.Equal(0, await Count(factory));
        using var valid = await client.PostAsync(Route + "/batch", Json("{\"items\":[" + Create + "," + Create + "]}"), Ct);
        var result = await Data(valid);
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("reused").GetInt32());
        Assert.Equal("import", result.GetProperty("items")[0].GetProperty("originKind").GetString());
        Assert.NotNull(result.GetProperty("items")[0].GetProperty("originReference").GetString());
        using var empty = await client.PostAsync(Route + "/batch", Json("{\"items\":[]}"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var oversized = await client.PostAsync(Route, Json(Create.PadRight(VocabularyRelationEndpoints.MaxRequestBytes + 1)), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task BoundariesRejectChunkedOversizeTooManyItemsAndInvalidQueries()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = await Client(factory);
        using var chunked = new ChunkedContent(Encoding.UTF8.GetBytes(Create.PadRight(VocabularyRelationEndpoints.MaxRequestBytes + 1)));
        using var tooLarge = await client.PostAsync(Route, chunked, Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        using var batch = await client.PostAsync(Route + "/batch",
            Json("{\"items\":[" + string.Join(',', Enumerable.Repeat(Create, 101)) + "]}"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        foreach (var query in new[] { "?direction=invalid", "?type=unknown", "?status=unknown", "?page=0", "?size=101" })
        {
            using var invalid = await client.GetAsync("/admin/vocabulary/write/relations" + query, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var missing = await client.GetAsync("/admin/vocabulary/missing/relations", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var noVersion = await client.DeleteAsync(Route + "/missing", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        using var noConfirmation = await client.PutAsync(Route + "/missing", Json(Create), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noConfirmation.StatusCode);
        Assert.Equal(0, await Count(factory));
    }

    [Fact]
    public async Task RenameAndCleanupEndpointsPreserveRelationIntegrityAndReportActualCounts()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = await Client(factory);
        using var response = await client.PostAsync(Route, Json(Create), Ct);
        var id = (await Data(response)).GetProperty("id").GetString();
        using var renamed = await client.PutAsJsonAsync("/admin/vocabulary/write", new { word = "rewrite", phoneticUk = (string?)null, phoneticUs = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using var relation = await client.GetAsync(Route + "/" + id, Ct);
        var changed = await Data(relation);
        Assert.Equal("word_changed", changed.GetProperty("reviewReason").GetString());
        Assert.Equal(2, changed.GetProperty("version").GetInt64());
        using var preview = await client.PostAsJsonAsync("/admin/vocabulary-books/book/cleanup/preview", new { action = "clear" }, Ct);
        var counts = await Data(preview);
        Assert.Equal(2, counts.GetProperty("orphanWordCount").GetInt32());
        Assert.Equal(2, counts.GetProperty("retainedRelatedWordCount").GetInt32());
        Assert.Equal(0, counts.GetProperty("deletableWordCount").GetInt32());
        using var commit = await client.PostAsJsonAsync("/admin/vocabulary-books/book/cleanup", new { action = "clear" }, Ct);
        var deleted = await Data(commit);
        Assert.Equal(0, deleted.GetProperty("deletedWordCount").GetInt32());
        Assert.Equal(2, deleted.GetProperty("retainedRelatedWordCount").GetInt32());
        Assert.Equal(1, await Count(factory));
    }

    private async Task<HttpClient> Client(VocabularyWebApplicationFactory factory, string role = "admin", bool cookie = false)
    {
        await Seed(factory);
        var client = factory.CreateClient();
        if (cookie) { client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie(role)); AdminTestAntiforgery.Attach(client, factory); }
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(role));
        return client;
    }

    private async Task Seed(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.Add(new VocabularyBookEntity { Id = "book", BookName = "Book", Status = true });
        foreach (var word in new[] { "write", "wrote" })
        {
            db.Vocabularies.Add(new VocabularyEntity { Id = word, Word = word });
            db.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = word + "-meaning", VocabularyId = word, BookId = "book", Meaning = word });
        }
        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> Count(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().VocabularyRelations.CountAsync(Ct);
    }
    private async Task<JsonElement> Data(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("data").Clone();
    }
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, TestContext.Current.CancellationToken).AsTask();
    }
}
