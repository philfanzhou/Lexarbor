using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Repositories;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class TextbookAudioTrackEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Path = "/admin/vocabulary-books/A/units/u/audio-tracks";
    private const string Version = "00000000-0000-0000-0000-000000000001";
    private const string Create = """{"title":" recording ","displayOrder":0}""";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "maintainer")]
    [InlineData(true, "maintainer")]
    public async Task Lifecycle_ExactDtoDisabledEmptyBookCookieBearerCustomRole(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role, ["Audio:Enabled"] = "false" });
        using var client = factory.CreateClient(); await SeedAsync(factory.Services);
        Authenticate(client, factory, cookie, role); if (cookie) AdminTestAntiforgery.Attach(client, factory);
        using var empty = await client.GetAsync(Path + "?page=0&size=0", Token);
        var emptyPage = await DataAsync(empty); Assert.Empty(emptyPage.GetProperty("items").EnumerateArray());
        Assert.Equal(0, emptyPage.GetProperty("totalCount").GetInt32()); Assert.Equal(0, emptyPage.GetProperty("totalPage").GetInt32());
        var tracks = new List<Track>();
        for (var i = 0; i < 3; i++)
        {
            using var created = await client.PostAsync(Path, Json(Create), Token);
            var data = await DataAsync(created);
            Assert.Equal(new[] { "bookId", "displayOrder", "durationSeconds", "hasFile", "id", "isAvailable", "title", "unitId", "version" }, data.EnumerateObject().Select(p => p.Name).Order());
            var track = data.Deserialize<Track>(JsonOptions)!;
            Assert.Equal("recording", track.Title); Assert.Equal("A", track.BookId); Assert.Equal("u", track.UnitId);
            Assert.False(track.HasFile); Assert.Null(track.DurationSeconds); Assert.False(track.IsAvailable);
            Assert.True(Guid.TryParseExact(track.Version, "D", out var version)); Assert.Equal(version.ToString("D"), track.Version);
            tracks.Add(track);
        }
        using var listed = await client.GetAsync(Path + "?page=1&size=2", Token);
        var page = await DataAsync(listed); Assert.Equal(3, page.GetProperty("totalCount").GetInt32()); Assert.Equal(2, page.GetProperty("totalPage").GetInt32());
        Assert.Equal(tracks.OrderBy(t => t.Id, StringComparer.Ordinal).Take(2).Select(t => t.Id), page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        using var capped = await client.GetAsync(Path + "?size=999&page=2147483647", Token);
        Assert.Empty((await DataAsync(capped)).GetProperty("items").EnumerateArray());
        var first = tracks[0];
        using var enableWithoutFile = await ReplaceAsync(client, first, true); await FailureAsync(enableWithoutFile, 409);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.AudioAssets.Add(new() { Id = "asset", Bucket = "test-only", ObjectKey = "test.mp3", Sha256 = new string('a', 64), ByteLength = 100, CreatedAtUnixMs = 1 });
            await db.SaveChangesAsync(Token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE textbook_audio_track SET current_asset_id='asset',duration_seconds=0.5 WHERE id={first.Id}", Token);
        }
        using var unpublished = await ReplaceAsync(client, first, true); await FailureAsync(unpublished, 409);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Database.ExecuteSqlRawAsync("UPDATE audio_asset SET first_published_at_unix_ms=2", Token);
        using var enabled = await ReplaceAsync(client, first, true); var current = (await DataAsync(enabled)).Deserialize<Track>(JsonOptions)!;
        Assert.True(current.IsAvailable); Assert.True(current.HasFile); Assert.Equal(0.5, current.DurationSeconds); Assert.NotEqual(first.Version, current.Version);
        using var stalePut = await ReplaceAsync(client, first, false); await FailureAsync(stalePut, 409);
        using var staleDelete = await client.DeleteAsync($"{Path}/{first.Id}?expectedVersion={first.Version}", Token); await FailureAsync(staleDelete, 409);
        using var same = await ReplaceAsync(client, current, true); var sameValue = (await DataAsync(same)).Deserialize<Track>(JsonOptions)!; Assert.NotEqual(current.Version, sameValue.Version);
        using var disabled = await ReplaceAsync(client, sameValue, false); var last = (await DataAsync(disabled)).Deserialize<Track>(JsonOptions)!;
        Assert.False(last.IsAvailable); Assert.True(last.HasFile); Assert.Equal(0.5, last.DurationSeconds);
        using var deleted = await client.DeleteAsync($"{Path}/{last.Id}?expectedVersion={last.Version}", Token);
        Assert.True((await DataAsync(deleted)).GetProperty("success").GetBoolean());

    }

    [Theory]
    [InlineData("GET", "anonymous", 401)]
    [InlineData("POST", "anonymous", 401)]
    [InlineData("PUT", "anonymous", 401)]
    [InlineData("DELETE", "anonymous", 401)]
    [InlineData("GET", "student", 403)]
    [InlineData("POST", "student", 403)]
    [InlineData("PUT", "student", 403)]
    [InlineData("DELETE", "student", 403)]
    [InlineData("POST", "cookie-no-csrf", 401)]
    [InlineData("PUT", "cookie-no-csrf", 401)]
    [InlineData("DELETE", "cookie-no-csrf", 401)]
    [InlineData("GET", "cookie-admin-bearer-student", 403)]
    [InlineData("POST", "cookie-admin-bearer-student", 403)]
    [InlineData("PUT", "cookie-admin-bearer-student", 403)]
    [InlineData("DELETE", "cookie-admin-bearer-student", 403)]
    public async Task AuthorizationAndBearerPriority_AllVerbsRejectWithoutWrites(string method, string identity, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient(); await SeedAsync(factory.Services, true);
        if (identity == "student") Authenticate(client, factory, false, "student");
        if (identity.StartsWith("cookie", StringComparison.Ordinal)) Authenticate(client, factory, true, "admin");
        if (identity == "cookie-admin-bearer-student") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("student"));
        using var response = await SendAsync(client, method);
        await FailureAsync(response, status); await AssertUnchangedAsync(factory.Services);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task AdminBearerOverridesStudentCookie_AllVerbs(string method)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient(); await SeedAsync(factory.Services, true);
        Authenticate(client, factory, true, "student"); Authenticate(client, factory, false, "admin");
        using var response = await SendAsync(client, method); await DataAsync(response);
    }

    [Theory]
    [InlineData("POST", "null")]
    [InlineData("POST", "{}")]
    [InlineData("POST", "[]")]
    [InlineData("POST", "{")]
    [InlineData("POST", "{\"title\":\"x\"}")]
    [InlineData("POST", "{\"displayOrder\":0}")]
    [InlineData("POST", "{\"title\":null,\"displayOrder\":0}")]
    [InlineData("POST", "{\"title\":\" \t \",\"displayOrder\":0}")]
    [InlineData("POST", "{\"title\":5,\"displayOrder\":0}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":null}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":-1}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":1.5}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":2147483648}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":\"0\"}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":0,\"currentAssetId\":\"asset\"}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":0,\"durationSeconds\":1}")]
    [InlineData("POST", "{\"title\":\"x\",\"displayOrder\":0,\"version\":\"x\"}")]
    [InlineData("PUT", "null")]
    [InlineData("PUT", "{}")]
    [InlineData("PUT", "{\"title\":\"x\",\"displayOrder\":0,\"isAvailable\":false}")]
    [InlineData("PUT", "{\"title\":\"x\",\"displayOrder\":0,\"isAvailable\":false,\"expectedVersion\":null}")]
    [InlineData("PUT", "{\"title\":\"x\",\"displayOrder\":0,\"expectedVersion\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("PUT", "{\"title\":\"x\",\"displayOrder\":0,\"isAvailable\":\"false\",\"expectedVersion\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("PUT", "{\"title\":\"x\",\"displayOrder\":0,\"isAvailable\":false,\"expectedVersion\":\"BAD\"}")]
    [InlineData("PUT", "{\"title\":\"x\",\"displayOrder\":0,\"isAvailable\":false,\"expectedVersion\":\"00000000-0000-0000-0000-00000000000A\"}")]
    [InlineData("PUT", "{\"title\":\"x\",\"displayOrder\":0,\"isAvailable\":false,\"expectedVersion\":\"00000000-0000-0000-0000-000000000001\",\"bookId\":\"B\"}")]
    public async Task StrictBodies_RejectMissingWrongTypeAndUnknownFields(string method, string body)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient(); await SeedAsync(factory.Services, true);
        Authenticate(client, factory, false, "admin");
        using var request = new HttpRequestMessage(new HttpMethod(method), Path + (method == "PUT" ? "/track" : "")) { Content = Json(body) };
        using var response = await client.SendAsync(request, Token); await FailureAsync(response, 400); await AssertUnchangedAsync(factory.Services);
    }

    [Fact]
    public async Task TitleLength_UsesTrimmedUtf16Units()
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient(); await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        using var allowed = await client.PostAsJsonAsync(Path, new { title = " " + string.Concat(Enumerable.Repeat("😀", 100)) + " ", displayOrder = int.MaxValue }, Token);
        await DataAsync(allowed);
        using var tooLong = await client.PostAsJsonAsync(Path, new { title = string.Concat(Enumerable.Repeat("😀", 100)) + "x", displayOrder = 0 }, Token);
        await FailureAsync(tooLong, 400);
    }

    [Theory]
    [InlineData("?page=-1")]
    [InlineData("?size=-1")]
    [InlineData("?page=2147483648")]
    [InlineData("?size=2147483648")]
    [InlineData("?page=x")]
    [InlineData("?size=1.5")]
    [InlineData("?page=")]
    [InlineData("?page=1&page=2")]
    public async Task InvalidPagination_Returns400(string query)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient(); await SeedAsync(factory.Services, true);
        Authenticate(client, factory, false, "admin");
        using var response = await client.GetAsync(Path + query, Token); await FailureAsync(response, 400); await AssertUnchangedAsync(factory.Services);
    }

    [Theory]
    [InlineData("missing", "u", "track", 404)]
    [InlineData("A", "missing", "track", 404)]
    [InlineData("B", "u", "track", 409)]
    [InlineData("A", "u", "missing", 404)]
    [InlineData("A", "u2", "track", 409)]
    public async Task OwnershipAndMissingTargets_AllApplicableVerbs(string book, string unit, string track, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient(); await SeedAsync(factory.Services, true);
        Authenticate(client, factory, false, "admin");
        var root = $"/admin/vocabulary-books/{book}/units/{unit}/audio-tracks";
        if (track == "track" && unit != "u2")
        {
            using var read = await client.GetAsync(root, Token); await FailureAsync(read, status);
            using var create = await client.PostAsync(root, Json(Create), Token); await FailureAsync(create, status);
        }
        using var put = await client.PutAsJsonAsync(root + "/" + track, new { title = "changed", displayOrder = 0, isAvailable = false, expectedVersion = Version }, Token); await FailureAsync(put, status);
        using var delete = await client.DeleteAsync(root + "/" + track + "?expectedVersion=" + Version, Token); await FailureAsync(delete, status);
        await AssertUnchangedAsync(factory.Services);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?expectedVersion=")]
    [InlineData("?expectedVersion=bad")]
    [InlineData("?expectedVersion=00000000-0000-0000-0000-000000000001&expectedVersion=00000000-0000-0000-0000-000000000001")]
    public async Task DeleteRequiresOneCanonicalVersion(string query)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient(); await SeedAsync(factory.Services, true); Authenticate(client, factory, false, "admin");
        using var response = await client.DeleteAsync(Path + "/track" + query, Token); await FailureAsync(response, 400); await AssertUnchangedAsync(factory.Services);
    }

    [Fact]
    public async Task ExternalWriter_Returns503AndRetryAfterWithoutWrites()
    {
        var directory = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-track-busy-{Guid.NewGuid():N}");
        var database = System.IO.Path.Combine(directory, "vocabulary.db");
        try
        {
            await using var factory = new VocabularyWebApplicationFactory(databasePath: database, environment: "Testing", includeAppCredentials: true,
                extraConfiguration: new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"Data Source={database};Pooling=False;Default Timeout=1" });
            using var client = factory.CreateClient(); await SeedAsync(factory.Services, true); Authenticate(client, factory, false, "admin");
            await using var blocker = new SqliteConnection($"Data Source={database};Pooling=False"); await blocker.OpenAsync(Token);
            await using (var transaction = blocker.BeginTransaction())
            {
                foreach (var verb in new[] { "POST", "PUT", "DELETE" })
                {
                    using var response = await SendAsync(client, verb); await FailureAsync(response, 503); Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
                }
            }
            await AssertUnchangedAsync(factory.Services);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExclusiveWriter_ListReturns503WithoutWrites_AndRecoversAfterUnlock()
    {
        var directory = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-track-fix-306-read-busy-{Guid.NewGuid():N}");
        var database = System.IO.Path.Combine(directory, "vocabulary.db");
        try
        {
            await using var factory = new VocabularyWebApplicationFactory(databasePath: database, environment: "Testing", includeAppCredentials: true,
                extraConfiguration: new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"Data Source={database};Pooling=False;Default Timeout=1" });
            using var client = factory.CreateClient(); await SeedAsync(factory.Services, true); Authenticate(client, factory, false, "admin");
            using var before = await client.GetAsync(Path, Token);
            var original = await DataAsync(before);
            await using var blocker = new SqliteConnection($"Data Source={database};Pooling=False"); await blocker.OpenAsync(Token);
            await using var command = blocker.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;";
            await command.ExecuteNonQueryAsync(Token);
            try
            {
                using var response = await client.GetAsync(Path, Token);
                await FailureAsync(response, 503);
                Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
                var body = await response.Content.ReadAsStringAsync(Token);
                Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(database, body, StringComparison.Ordinal);
            }
            finally { command.CommandText = "ROLLBACK"; await command.ExecuteNonQueryAsync(Token); }
            await AssertUnchangedAsync(factory.Services);
            using var recovered = await client.GetAsync(Path, Token);
            Assert.Equal(original.GetRawText(), (await DataAsync(recovered)).GetRawText());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ParentDeletesAndCleanupPreview_ReturnSafe409WithoutPartialVocabularyChanges()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient(); await SeedAsync(factory.Services, true); Authenticate(client, factory, false, "admin");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.Add(new() { Id = "word", Word = "retained" });
            db.VocabularyMeanings.Add(new() { Id = "meaning", VocabularyId = "word", BookId = "A", Meaning = "retained" });
            db.VocabularyMeaningUnits.Add(new() { BookId = "A", UnitId = "u", MeaningId = "meaning" });
            await db.SaveChangesAsync(Token);
        }
        using var book = await client.DeleteAsync("/admin/vocabulary-books/A", Token); await FailureAsync(book, 409);
        using var unit = await client.DeleteAsync("/admin/vocabulary-books/A/units/u", Token); await FailureAsync(unit, 409);
        foreach (var suffix in new[] { "/preview", "" })
        {
            using var cleanup = await client.PostAsJsonAsync("/admin/vocabulary-books/A/cleanup" + suffix,
                suffix.Length > 0 ? (object)new { action = "delete" } : new { action = "delete", confirmedBookName = "A" }, Token);
            await FailureAsync(cleanup, 409);
        }
        await AssertUnchangedAsync(factory.Services);
        using var verify = factory.Services.CreateScope(); var stored = verify.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Single(await stored.Vocabularies.ToArrayAsync(Token)); Assert.Single(await stored.VocabularyMeanings.ToArrayAsync(Token));
        Assert.Single(await stored.VocabularyMeaningUnits.ToArrayAsync(Token)); Assert.Equal(2, await stored.VocabularyBookUnits.CountAsync(Token));
        Assert.Equal(2, await stored.VocabularyBooks.CountAsync(Token));
    }

    [Fact]
    public async Task CatalogPaths_NeverCallObjectStore()
    {
        var store = new Mock<IAudioObjectStore>(MockBehavior.Strict);
        await using var factory = new VocabularyWebApplicationFactory();
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IAudioObjectStore>(); services.AddSingleton(store.Object); }));
        using var client = configured.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await SeedAsync(configured.Services, true); Authenticate(client, factory, false, "admin");
        using var denied = await client.PutAsJsonAsync(Path + "/track", new { title = "recording", displayOrder = 0, isAvailable = true, expectedVersion = Version }, Token);
        await FailureAsync(denied, 409);
        foreach (var method in new[] { "GET", "POST", "PUT" }) { using var response = await SendAsync(client, method); await DataAsync(response); }
        using var list = await client.GetAsync(Path, Token); var page = await DataAsync(list);
        var track = page.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "track");
        using var removed = await client.DeleteAsync(Path + "/track?expectedVersion=" + track.GetProperty("version").GetString(), Token);
        await DataAsync(removed); store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PathIds_AreTrimmedOpaqueStringsAndCannotBeBlank()
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        await SeedAsync(factory.Services, true); Authenticate(client, factory, false, "admin");
        using var padded = await client.GetAsync("/admin/vocabulary-books/%20A%20/units/%20u%20/audio-tracks", Token); await DataAsync(padded);
        using var blankBook = await client.GetAsync("/admin/vocabulary-books/%20/units/u/audio-tracks", Token); await FailureAsync(blankBook, 400);
        using var blankUnit = await client.GetAsync("/admin/vocabulary-books/A/units/%20/audio-tracks", Token); await FailureAsync(blankUnit, 400);
        using var blankTrack = await client.DeleteAsync(Path + "/%20?expectedVersion=" + Version, Token); await FailureAsync(blankTrack, 400);
        await AssertUnchangedAsync(factory.Services);
    }

    private static Task<HttpResponseMessage> ReplaceAsync(HttpClient client, Track track, bool available) => client.PutAsJsonAsync(Path + "/" + track.Id,
        new { title = track.Title, displayOrder = track.DisplayOrder, isAvailable = available, expectedVersion = track.Version }, Token);
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method) => method switch
    {
        "GET" => client.GetAsync(Path, Token),
        "POST" => client.PostAsync(Path, Json(Create), Token),
        "PUT" => client.PutAsJsonAsync(Path + "/track", new { title = "recording", displayOrder = 0, isAvailable = false, expectedVersion = Version }, Token),
        _ => client.DeleteAsync(Path + "/track?expectedVersion=" + Version, Token)
    };
    private static void Authenticate(HttpClient client, VocabularyWebApplicationFactory factory, bool cookie, string role)
    { if (cookie) client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie(role)); else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(role)); }
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)); Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        return json.RootElement.GetProperty("data").Clone();
    }
    private static async Task FailureAsync(HttpResponseMessage response, int status)
    { await HttpFailureAssertions.AssertFailureAsync(response, (HttpStatusCode)status); Headers(response); }
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore); Assert.Equal("no-cache", response.Headers.GetValues("Pragma").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single()); Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
    private static async Task SeedAsync(IServiceProvider services, bool track = false)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = false }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = true });
        db.VocabularyBookUnits.AddRange(new VocabularyBookUnitEntity { Id = "u", BookId = "A", Number = 1 }, new VocabularyBookUnitEntity { Id = "u2", BookId = "A", Number = 2 });
        if (track) db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "A", UnitId = "u", Title = "original", Version = Version });
        await db.SaveChangesAsync(Token);
    }
    private static async Task AssertUnchangedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var track = Assert.Single(await db.TextbookAudioTracks.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal("original", track.Title); Assert.Equal(Version, track.Version); Assert.False(track.IsAvailable); Assert.Null(track.CurrentAssetId);
    }
    private sealed record Track(string Id, string BookId, string UnitId, string Title, int DisplayOrder, bool HasFile, double? DurationSeconds, bool IsAvailable, string Version);
}
