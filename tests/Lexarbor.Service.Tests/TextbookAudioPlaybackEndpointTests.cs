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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service.Tests;

public sealed class TextbookAudioPlaybackEndpointTests
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
        public Host(bool enabled = true, int timeout = 60, int ttl = 900)
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
                ["RateLimits:PublicApi:PermitLimit"] = "1",
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
            Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        public async Task SeedAsync(string? asset = "published")
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = false }, new VocabularyBookEntity { Id = "B", BookName = "B" });
            db.VocabularyBookUnits.AddRange(new VocabularyBookUnitEntity { Id = "u", BookId = "A", Number = 1 }, new VocabularyBookUnitEntity { Id = "u2", BookId = "A", Number = 2 }, new VocabularyBookUnitEntity { Id = "v", BookId = "B", Number = 1 });
            if (asset != null) db.AudioAssets.Add(new() { Id = asset, Bucket = "test-private-bucket", ObjectKey = "audio/private-test-key.mp3", Sha256 = new string('a', 64), ByteLength = 128, CreatedAtUnixMs = 1, FirstPublishedAtUnixMs = asset == "published" ? 2 : null });
            db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "A", UnitId = "u", Title = "Recording", CurrentAssetId = asset, DurationSeconds = asset != null ? 1 : null, Version = Guid.NewGuid().ToString("D") });
            await db.SaveChangesAsync(Ct);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
    private static string Route(string book = "A", string unit = "u", string track = "track")
        => $"/admin/vocabulary-books/{book}/units/{unit}/audio-tracks/{track}/playback";
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-cache", response.Headers.GetValues("Pragma").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledEmptyBookAndUnavailableTrack_AdminCanPlayExactDto(bool cookie)
    {
        await using var host = new Host(ttl: 120); await host.SeedAsync();
        if (cookie) { host.Client.DefaultRequestHeaders.Authorization = null; host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("admin")); }
        using var response = await host.Client.GetAsync(Route("%20A%20", "%20u%20", "%20track%20"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.GetProperty("success").GetBoolean()); var data = json.RootElement.GetProperty("data");
        Assert.Equal(new[] { "expiresAt", "trackId", "url" }, data.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal("track", data.GetProperty("trackId").GetString()); Assert.Equal(TimeSpan.Zero, data.GetProperty("expiresAt").GetDateTimeOffset().Offset);
        Assert.Equal("https://audio.example.test/synthetic-signed-link", data.GetProperty("url").GetString());
        Assert.Equal(TimeSpan.FromSeconds(120), host.Store.Validity); Assert.Single(host.Store.Stats); Assert.Equal(host.Store.Stats, host.Store.Signs);
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains("synthetic-signed-link", StringComparison.Ordinal) || line.Contains("private-test-key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie-student", 403)]
    [InlineData("cookie-admin-bearer-student", 403)]
    [InlineData("cookie-admin-bearer-invalid", 401)]
    public async Task AuthorizationPrecedesInputsAndRuntime_BearerWins(string identity, int status)
    {
        await using var host = new Host(false); host.Client.DefaultRequestHeaders.Authorization = null;
        if (identity.StartsWith("cookie", StringComparison.Ordinal)) host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie(identity == "cookie-student" ? "student" : "admin"));
        if (identity == "student" || identity.Contains("bearer-student", StringComparison.Ordinal)) host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Factory.CreateToken("student"));
        if (identity.EndsWith("invalid", StringComparison.Ordinal)) host.Client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        using var response = await host.Client.GetAsync(Route("%20"), Ct); Assert.Equal(status, (int)response.StatusCode); Headers(response); Assert.Empty(host.Store.Stats);
    }

    [Fact]
    public async Task AdminBearerOverridesStudentCookieAndCustomRoleIsAccepted()
    {
        await using var host = new Host(); await host.SeedAsync(); host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("student"));
        using var response = await host.Client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response);
        await using var factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = "maintainer" });
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("maintainer"));
        using var accepted = await client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, accepted.StatusCode); Headers(accepted);
    }

    [Theory]
    [InlineData("%20", "u", "track", 400)]
    [InlineData("A", "%20", "track", 400)]
    [InlineData("A", "u", "%20", 400)]
    [InlineData("missing", "u", "track", 404)]
    [InlineData("A", "missing", "track", 404)]
    [InlineData("A", "u", "missing", 404)]
    [InlineData("B", "u", "track", 409)]
    [InlineData("A", "u2", "track", 409)]
    [InlineData("B", "v", "track", 409)]
    public async Task MissingOwnershipAndInvalidIds_DoNotContactStorage(string book, string unit, string track, int status)
    {
        await using var host = new Host(); await host.SeedAsync(); using var response = await host.Client.GetAsync(Route(book, unit, track), Ct);
        await HttpFailureAssertions.AssertFailureAsync(response, (HttpStatusCode)status); Headers(response); Assert.Empty(host.Store.Stats); Assert.Empty(host.Store.Signs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unpublished")]
    public async Task NoPublishedCurrentFile_Returns404(string? asset)
    {
        await using var host = new Host(); await host.SeedAsync(asset); using var response = await host.Client.GetAsync(Route(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Headers(response); Assert.Empty(host.Store.Stats);
    }

    [Fact]
    public async Task DisabledAndUnexpectedReadFailure_RetainSafeHeaders()
    {
        await using var host = new Host(false); using var disabled = await host.Client.GetAsync(Route(), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode); Headers(disabled); Assert.Contains("Audio storage is disabled.", await disabled.Content.ReadAsStringAsync(Ct));
        await using var enabled = new Host(); await enabled.SeedAsync();
        using (var scope = enabled.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Database.ExecuteSqlRawAsync("DROP TABLE textbook_audio_track", Ct);
        using var failure = await enabled.Client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode); Headers(failure);
        Assert.DoesNotContain("SELECT", await failure.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("stat-timeout")]
    [InlineData("sign-failure")]
    [InlineData("sign-timeout")]
    public async Task StorageFailures_ReturnFixed503_NoFallbackOrSensitiveLogs(string failure)
    {
        await using var host = new Host(timeout: 1); await host.SeedAsync(); const string marker = "synthetic-provider-sensitive-url-key";
        host.Store.OnStat = failure switch { "missing" => _ => Task.FromResult<AudioObjectInfo?>(null), "denied" => _ => throw new InvalidOperationException(marker), "stat-timeout" => async ct => { await Task.Delay(Timeout.Infinite, ct); return null; }, _ => null };
        if (failure == "sign-failure") host.Store.OnSign = _ => throw new InvalidOperationException(marker);
        if (failure == "sign-timeout") host.Store.OnSign = async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        using var response = await host.Client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Headers(response);
        Assert.Equal("{\"success\":false,\"message\":\"Audio storage is unavailable.\"}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Single(host.Store.Stats); Assert.Equal(failure.StartsWith("sign", StringComparison.Ordinal) ? 1 : 0, host.Store.Signs.Count);
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExclusiveDatabaseLock_ReturnsSafe503RetryAfterAndRecovers()
    {
        var directory = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-textbook-playback-busy-{Guid.NewGuid():N}");
        var path = System.IO.Path.Combine(directory, "vocabulary.db");
        try
        {
            await using var factory = new VocabularyWebApplicationFactory("Testing", true, databasePath: path,
                extraConfiguration: new Dictionary<string, string?> { ["Audio:Enabled"] = "true", ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/", ["Audio:PublicEndpoint"] = "https://audio.example.test/", ["Audio:AccessKey"] = "synthetic-access", ["Audio:SecretKey"] = "synthetic-secret", ["ConnectionStrings:Default"] = $"Data Source={path};Pooling=False;Default Timeout=1" });
            var store = new Store();
            await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(new StoreFactory(store)); }));
            using var client = app.CreateClient(new() { BaseAddress = new("https://localhost") }); client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin"));
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                db.VocabularyBooks.Add(new() { Id = "A", BookName = "A" }); db.VocabularyBookUnits.Add(new() { Id = "u", BookId = "A", Number = 1 });
                db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "A", UnitId = "u", Title = "Track", Version = Guid.NewGuid().ToString("D") }); await db.SaveChangesAsync(Ct);
            }
            await using var blocker = new SqliteConnection($"Data Source={path};Pooling=False"); await blocker.OpenAsync(Ct);
            await using var command = blocker.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;"; await command.ExecuteNonQueryAsync(Ct);
            try
            {
                using var response = await client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Headers(response);
                Assert.Equal("1", response.Headers.GetValues("Retry-After").Single()); var body = await response.Content.ReadAsStringAsync(Ct);
                Assert.DoesNotContain(path, body); Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase); Assert.Empty(store.Stats);
            }
            finally { command.CommandText = "ROLLBACK"; await command.ExecuteNonQueryAsync(Ct); }
            using var recovered = await client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.NotFound, recovered.StatusCode); Headers(recovered);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_StopsWaitingAtStatAndSign(bool signing)
    {
        await using var host = new Host(); await host.SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause(CancellationToken ct) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
        if (signing) host.Store.OnSign = async ct => { await Pause(ct); return null!; };
        else host.Store.OnStat = async ct => { await Pause(ct); return null; };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = host.Client.GetAsync(Route(), cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(signing ? 1 : 0, host.Store.Signs.Count);
    }
}
