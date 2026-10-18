using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Audio;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service.Tests;

public sealed class PublicTextbookAudioEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Store : IAudioObjectStore
    {
        public readonly List<AudioObjectLocation> Stats = [];
        public readonly List<AudioObjectLocation> Signs = [];
        public Func<CancellationToken, Task<AudioObjectInfo?>>? OnStat;
        public Func<CancellationToken, Task<AudioPlaybackLink>>? OnSign;
        public TimeSpan Validity;
        public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token)
            => PutAsync(location, content, length, type, AudioUploadPurpose.Vocabulary, token);
        public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, AudioUploadPurpose purpose, CancellationToken token)
            => throw new InvalidOperationException("Playback cannot upload.");
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token)
            => throw new InvalidOperationException("Playback cannot delete.");
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token)
        {
            Stats.Add(location);
            return OnStat?.Invoke(token) ?? Task.FromResult<AudioObjectInfo?>(new(128, "audio/mpeg"));
        }
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token)
        {
            Signs.Add(location); Validity = validity;
            return OnSign?.Invoke(token) ?? Task.FromResult(new AudioPlaybackLink("https://audio.example.test/synthetic-signed-link",
                new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(8))));
        }
    }
    private sealed class StoreFactory(Store store) : IAudioObjectStoreFactory
    {
        public IAudioObjectStore Create(AudioOptions options) => store;
    }
    private sealed class Logs : ILoggerProvider
    {
        public readonly ConcurrentBag<string> Messages = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(Messages);
        public void Dispose() { }
        private sealed class Recorder(ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, error) + error?.ToString());
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly Store Store = new();
        public readonly Logs Logs = new();
        public readonly VocabularyWebApplicationFactory Factory;
        public readonly WebApplicationFactory<Program> App;
        public readonly HttpClient Client;
        public Host(bool enabled = true, int timeout = 60, int ttl = 900, int permits = 1000)
        {
            Factory = new("Testing", true, extraConfiguration: new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = enabled.ToString(),
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:PublicEndpoint"] = "https://audio.example.test/",
                ["Audio:AccessKey"] = "synthetic-access",
                ["Audio:SecretKey"] = "synthetic-secret",
                ["Audio:OperationTimeoutSeconds"] = timeout.ToString(),
                ["Audio:PlaybackTtlSeconds"] = ttl.ToString(),
                ["RateLimits:PublicApi:Enabled"] = "true",
                ["RateLimits:PublicApi:PermitLimit"] = permits.ToString(),
                ["RateLimits:PublicApi:WindowSeconds"] = "300",
                ["RateLimits:AdminLogin:Enabled"] = "true",
                ["RateLimits:AdminLogin:PermitLimit"] = "1",
                ["RateLimits:AdminLogin:WindowSeconds"] = "300"
            });
            App = Factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddProvider(Logs));
                builder.ConfigureServices(services => { services.RemoveAll<IAudioObjectStore>(); services.AddSingleton<IAudioObjectStore>(Store); services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(new StoreFactory(Store)); });
            });
            Client = App.CreateClient(new() { BaseAddress = new("https://localhost") });

        }
        public async Task SeedAsync(string? asset = "published")
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = true }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = true });
            db.VocabularyBookUnits.AddRange(new VocabularyBookUnitEntity { Id = "u", BookId = "A", Number = 1 }, new VocabularyBookUnitEntity { Id = "u2", BookId = "A", Number = 2 }, new VocabularyBookUnitEntity { Id = "v", BookId = "B", Number = 1 });
            if (asset != null) db.AudioAssets.Add(new() { Id = asset, Bucket = "test-private-bucket", ObjectKey = "audio/private-test-key.mp3", Sha256 = new string('a', 64), ByteLength = 128, CreatedAtUnixMs = 1, FirstPublishedAtUnixMs = asset == "published" ? 2 : null });
            db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "A", UnitId = "u", Title = "Recording", IsAvailable = asset != null, CurrentAssetId = asset, DurationSeconds = asset != null ? 1 : null, Version = Guid.NewGuid().ToString("D") });
            await db.SaveChangesAsync(Ct);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
    private static string List(string book = "A", string unit = "u") => $"/api/vocabulary-books/{book}/units/{unit}/audio-tracks";
    private static string Play(string book = "A", string unit = "u", string track = "track") => List(book, unit) + $"/{track}/playback";
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.GetProperty("success").GetBoolean()); return json.RootElement.GetProperty("data").Clone();
    }
    private static async Task Sql(Host host, string sql)
    {
        using var scope = host.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Database.ExecuteSqlRawAsync(sql, Ct);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("student")]
    [InlineData("invalid")]
    [InlineData("cookie-student")]
    public async Task PublicIdentity_EmptyBookAndExactDtos_NoS3ForList(string identity)
    {
        await using var host = new Host(ttl: 120); await host.SeedAsync();
        if (identity == "cookie-student") host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("student"));
        else if (identity != "anonymous") host.Client.DefaultRequestHeaders.Authorization = new("Bearer", identity == "invalid" ? "invalid" : host.Factory.CreateToken("student"));
        using var listed = await host.Client.GetAsync(List("%20A%20", "%20u%20"), Ct); var page = await Data(listed);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "bookId", "displayOrder", "durationSeconds", "id", "title", "unitId", "version" }, item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("track", item.GetProperty("id").GetString()); Assert.Equal(1, item.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32()); Assert.Empty(host.Store.Stats); Assert.Empty(host.Store.Signs);
        using var played = await host.Client.GetAsync(Play(), Ct); var data = await Data(played);
        Assert.Equal(new[] { "expiresAt", "trackId", "url" }, data.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("track", data.GetProperty("trackId").GetString()); Assert.Equal(TimeSpan.Zero, data.GetProperty("expiresAt").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.FromSeconds(120), host.Store.Validity); Assert.Equal(host.Store.Stats, host.Store.Signs); Assert.Single(host.Store.Stats);
        Assert.False(played.Headers.Contains("X-Frame-Options"));
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains("private-test-key", StringComparison.Ordinal) || line.Contains("synthetic-signed-link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paging_UsesFilteredStableOrder_DefaultsCapAndLongOffset()
    {
        await using var host = new Host(); await host.SeedAsync();
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.AudioAssets.Add(new() { Id = "unpublished", Bucket = "test-private-bucket", ObjectKey = "audio/unpublished.mp3", Sha256 = new string('b', 64), ByteLength = 128, CreatedAtUnixMs = 1 });
            for (var i = 0; i < 105; i++) db.TextbookAudioTracks.Add(new() { Id = $"a{i:D3}", BookId = "A", UnitId = "u", Title = "Recording", IsAvailable = true, CurrentAssetId = "published", DurationSeconds = 2, Version = Guid.NewGuid().ToString("D") });
            db.TextbookAudioTracks.AddRange(new TextbookAudioTrackEntity { Id = "hidden", BookId = "A", UnitId = "u", Title = "Hidden", CurrentAssetId = "published", DurationSeconds = 1, Version = Guid.NewGuid().ToString("D") },
                new TextbookAudioTrackEntity { Id = "unpublished", BookId = "A", UnitId = "u", Title = "Unpublished", IsAvailable = false, CurrentAssetId = "unpublished", DurationSeconds = 1, Version = Guid.NewGuid().ToString("D") },
                new TextbookAudioTrackEntity { Id = "empty", BookId = "A", UnitId = "u", Title = "Empty", Version = Guid.NewGuid().ToString("D") },
                new TextbookAudioTrackEntity { Id = "other", BookId = "B", UnitId = "v", Title = "Other", IsAvailable = true, CurrentAssetId = "published", DurationSeconds = 1, Version = Guid.NewGuid().ToString("D") });
            await db.SaveChangesAsync(Ct); Assert.Empty(await db.VocabularyMeanings.ToArrayAsync(Ct));
        }
        foreach (var query in new[] { "", "?page=0&size=0", "?page=1&size=20" })
        {
            using var response = await host.Client.GetAsync(List() + query, Ct); var page = await Data(response);
            Assert.Equal(106, page.GetProperty("totalCount").GetInt32()); Assert.Equal(6, page.GetProperty("totalPage").GetInt32());
            Assert.Equal(Enumerable.Range(0, 20).Select(i => $"a{i:D3}"), page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        }
        using var second = await host.Client.GetAsync(List() + "?page=2&size=1000", Ct); var secondPage = await Data(second);
        Assert.Equal(2, secondPage.GetProperty("totalPage").GetInt32()); Assert.Equal(6, secondPage.GetProperty("items").GetArrayLength());
        using var far = await host.Client.GetAsync(List() + "?page=2147483647&size=100", Ct); var farPage = await Data(far);
        Assert.Empty(farPage.GetProperty("items").EnumerateArray()); Assert.Equal(106, farPage.GetProperty("totalCount").GetInt32());
        using var empty = await host.Client.GetAsync(List(unit: "u2"), Ct); var emptyPage = await Data(empty);
        Assert.Empty(emptyPage.GetProperty("items").EnumerateArray()); Assert.Equal(0, emptyPage.GetProperty("totalPage").GetInt32());
        foreach (var track in new[] { "hidden", "unpublished", "empty", "other", "missing" })
        { using var response = await host.Client.GetAsync(Play(track: track), Ct); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); }
        Assert.Empty(host.Store.Stats); Assert.Empty(host.Store.Signs);
    }

    [Theory]
    [InlineData("?page=-1")]
    [InlineData("?page=1&page=2")]
    [InlineData("?size=1&size=1")]
    [InlineData("?size=2147483648")]
    [InlineData("?page=+1")]
    [InlineData("?size=1.0")]
    [InlineData("?page=")]
    public async Task InvalidPaging_PrecedesDisabledRuntime(string query)
    {
        await using var host = new Host(false);
        using var response = await host.Client.GetAsync(List() + query, Ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(host.Store.Stats);
    }

    [Theory]
    [InlineData("%20", "u", "track", 400)]
    [InlineData("A", "%20", "track", 400)]
    [InlineData("A", "u", "%20", 400)]
    [InlineData("missing", "u", "track", 404)]
    [InlineData("A", "missing", "track", 404)]
    [InlineData("A", "v", "track", 404)]
    [InlineData("B", "v", "track", 404)]
    [InlineData("A", "u2", "track", 404)]
    public async Task Ownership_Is404AndInputsAreOpaque(string book, string unit, string track, int status)
    {
        await using var host = new Host(); await host.SeedAsync();
        using var response = await host.Client.GetAsync(Play(book, unit, track), Ct); Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); Assert.Empty(host.Store.Stats);
        if (status == 404) Assert.Contains("vocabulary.not_found", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledBookOrRuntime_PrecedesTarget_AndUnavailableIsAdminOnly(bool list)
    {
        await using var host = new Host(); await host.SeedAsync();
        await Sql(host, "UPDATE vocabulary_book SET status=0 WHERE id='A'; UPDATE textbook_audio_track SET is_available=0");
        using (var disabled = await host.Client.GetAsync(list ? List(unit: "missing") : Play(unit: "missing"), Ct))
        { Assert.Equal(HttpStatusCode.UnprocessableEntity, disabled.StatusCode); Assert.True(disabled.Headers.CacheControl?.NoStore); Assert.Contains("vocabulary.business_rule", await disabled.Content.ReadAsStringAsync(Ct)); }
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Factory.CreateToken("admin"));
        using (var admin = await host.Client.GetAsync(Play().Replace("/api/", "/admin/", StringComparison.Ordinal), Ct)) await Data(admin);
        await Sql(host, "UPDATE vocabulary_book SET status=1 WHERE id='A'");
        using var hidden = await host.Client.GetAsync(Play(), Ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var options = host.App.Services.GetRequiredService<AudioRuntimeConfiguration>(); var config = options.CopyCurrentOptions(); config.Enabled = false;
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = config }));
        using var candidate = await options.PrepareAsync(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonStream(json).Build(), Ct); Assert.NotNull(candidate); candidate.Publish();
        using var closed = await host.Client.GetAsync(list ? List("missing") : Play("missing"), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode); Assert.True(closed.Headers.CacheControl?.NoStore);
        Assert.Contains("Audio storage is disabled.", await closed.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stat")]
    [InlineData("sign")]
    public async Task ObjectFailure_IsSafe503WhileMetadataRemainsAvailable(string failure)
    {
        await using var host = new Host(); await host.SeedAsync();
        if (failure == "missing") host.Store.OnStat = _ => Task.FromResult<AudioObjectInfo?>(null);
        else if (failure == "stat") host.Store.OnStat = _ => throw new InvalidOperationException("synthetic-provider-secret-private-test-key");
        else host.Store.OnSign = _ => throw new InvalidOperationException("synthetic-provider-secret-private-test-key");
        using var response = await host.Client.GetAsync(Play(), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"success\":false,\"message\":\"Audio storage is unavailable.\"}", await response.Content.ReadAsStringAsync(Ct));
        using var list = await host.Client.GetAsync(List(), Ct); Assert.Single((await Data(list)).GetProperty("items").EnumerateArray()); Assert.Single(host.Store.Stats);
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains("synthetic-provider-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPublicBudget_CoversBothRoutesAndNoStoreRejection(bool list)
    {
        await using var host = new Host(permits: 1); await host.SeedAsync();
        using (var first = await host.Client.GetAsync(list ? List() : Play(), Ct)) await Data(first);
        using (var limited = await host.Client.GetAsync(list ? Play() : List(), Ct))
        { Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.True(limited.Headers.CacheControl?.NoStore); Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) > 0); }
        using (var old = await host.Client.GetAsync("/api/vocabulary-books/A/entries", Ct)) Assert.Equal(HttpStatusCode.TooManyRequests, old.StatusCode);
        // Toggling audio does not create a fresh public quota partition.
        var runtime = host.App.Services.GetRequiredService<AudioRuntimeConfiguration>();
        foreach (var enabled in new[] { false, true })
        {
            var options = runtime.CopyCurrentOptions(); options.Enabled = enabled;
            using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }));
            using var candidate = await runtime.PrepareAsync(new ConfigurationBuilder().AddJsonStream(stream).Build(), Ct); Assert.NotNull(candidate); candidate.Publish();
            using var limited = await host.Client.GetAsync(List(), Ct); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.True(limited.Headers.CacheControl?.NoStore);
        }
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Factory.CreateToken("admin"));
        using var admin = await host.Client.GetAsync(Play().Replace("/api/", "/admin/", StringComparison.Ordinal), Ct); await Data(admin);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExclusiveDatabaseLock_ReturnsSafe503RetryAfterAndRecovers(bool list)
    {
        var directory = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-textbook-playback-busy-{Guid.NewGuid():N}");
        var path = System.IO.Path.Combine(directory, "vocabulary.db");
        try
        {
            await using var factory = new VocabularyWebApplicationFactory("Testing", true, databasePath: path,
                extraConfiguration: new Dictionary<string, string?> { ["Audio:Enabled"] = "true", ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/", ["Audio:PublicEndpoint"] = "https://audio.example.test/", ["Audio:AccessKey"] = "synthetic-access", ["Audio:SecretKey"] = "synthetic-secret", ["ConnectionStrings:Default"] = $"Data Source={path};Pooling=False;Default Timeout=1" });
            var store = new Store();
            await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(new StoreFactory(store)); }));
            using var client = app.CreateClient(new() { BaseAddress = new("https://localhost") });
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                db.VocabularyBooks.Add(new() { Id = "A", BookName = "A", Status = true }); db.VocabularyBookUnits.Add(new() { Id = "u", BookId = "A", Number = 1 });
                db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "A", UnitId = "u", Title = "Track", Version = Guid.NewGuid().ToString("D") }); await db.SaveChangesAsync(Ct);
            }
            await using var blocker = new SqliteConnection($"Data Source={path};Pooling=False"); await blocker.OpenAsync(Ct);
            await using var command = blocker.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;"; await command.ExecuteNonQueryAsync(Ct);
            try
            {
                using var response = await client.GetAsync(list ? List() : Play(), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal("1", response.Headers.GetValues("Retry-After").Single()); var body = await response.Content.ReadAsStringAsync(Ct);
                Assert.DoesNotContain(path, body); Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase); Assert.Empty(store.Stats);
            }
            finally { command.CommandText = "ROLLBACK"; await command.ExecuteNonQueryAsync(Ct); }
            using var recovered = await client.GetAsync(list ? List() : Play(), Ct); Assert.Equal(list ? HttpStatusCode.OK : HttpStatusCode.NotFound, recovered.StatusCode); Assert.True(recovered.Headers.CacheControl?.NoStore);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedDatabaseFailure_IsSafe500NoStore(bool list)
    {
        await using var host = new Host(); await host.SeedAsync(); await Sql(host, "DROP TABLE textbook_audio_track");
        using var response = await host.Client.GetAsync(list ? List() : Play(), Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("SELECT", await response.Content.ReadAsStringAsync(Ct)); Assert.Empty(host.Store.Stats);
    }
}
