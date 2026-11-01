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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service.Tests;

public sealed class AudioPublicPlaybackEndpointTests
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
        public Host(bool enabled = true, int timeout = 60, int ttl = 900, int permits = 100)
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
            Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        public async Task SeedAsync()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.AddRange(new() { Id = "w", Word = "take off", PhoneticUk = "uk", PhoneticUs = "us" },
                new() { Id = "orphan", Word = "orphan" }, new() { Id = "other", Word = "other" });
            db.VocabularyBooks.AddRange(new() { Id = "enabled", BookName = "Enabled", Status = true },
                new() { Id = "disabled", BookName = "Disabled", Status = false });
            db.VocabularyMeanings.AddRange(new() { Id = "m", VocabularyId = "w", BookId = "enabled", PartOfSpeech = "v.", Meaning = "leave", PhoneticUk = "uk", PhoneticUs = "us" },
                new() { Id = "foreign-m", VocabularyId = "w", BookId = "disabled", Meaning = "foreign" },
                new() { Id = "other-m", VocabularyId = "other", BookId = "disabled", Meaning = "other" });
            await db.SaveChangesAsync(Ct);
        }
        public async Task<string> Bind(string locale = "en-GB", string? meaning = null, bool stale = false, string word = "w")
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var snapshot = await scope.ServiceProvider.GetRequiredService<IAudioReadRepository>().GetSnapshotAsync(word, meaning, Ct);
            var id = Guid.NewGuid().ToString();
            db.AudioAssets.Add(new AudioAssetEntity
            {
                Id = id,
                Bucket = "lexarbor-audio",
                ObjectKey = "audio/" + Guid.NewGuid().ToString("N") + ".mp3",
                Sha256 = new string('a', 64),
                ByteLength = 128,
                CreatedAtUnixMs = 1,
                FirstPublishedAtUnixMs = 2
            });
            db.VocabularyAudioBindings.Add(new VocabularyAudioBindingEntity
            {
                Id = id,
                WordId = word,
                MeaningId = meaning,
                Locale = AudioLocales.FromCode(locale),
                AssetId = id,
                SourceRevision = stale ? new string('b', 64) : AudioSelectionService.Select(snapshot, AudioLocales.FromCode(locale)).SourceRevision
            });
            await db.SaveChangesAsync(Ct); return id;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
    private static string Route(string? locale = "en-GB", string? meaning = null, string word = "w", string book = "enabled")
        => "/api/vocabulary/" + word + "/audio?bookId=" + book + (locale == null ? "" : "&locale=" + locale)
            + (meaning == null ? "" : "&meaningId=" + meaning);
    private static async Task<JsonDocument> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("en-GB", null, false)]
    [InlineData("en-US", null, true)]
    [InlineData("en-GB", "m", true)]
    [InlineData("en-US", "m", false)]
    public async Task AnonymousSelection_DefaultAndMeaningStale_AndUtcExpiry(string locale, string? meaning, bool stale)
    {
        await using var host = new Host(ttl: 120); await host.SeedAsync();
        host.Client.DefaultRequestHeaders.Authorization = null;
        var fallback = await host.Bind(locale, stale: meaning == null && stale);
        var own = meaning == null ? null : await host.Bind(locale, meaning, stale);
        using var response = await host.Client.GetAsync(Route(locale, meaning), Ct); using var json = await Read(response);
        var data = Assert.Single(json.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal(own ?? fallback, data.GetProperty("assetId").GetString());
        Assert.Equal(stale ? "stale" : "ready", data.GetProperty("status").GetString());
        Assert.Equal(meaning == null ? "default" : "meaning", data.GetProperty("source").GetString());
        Assert.Equal(TimeSpan.Zero, data.GetProperty("expiresAt").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.FromSeconds(120), host.Store.Validity);
        Assert.Single(host.Store.Signs); Assert.Equal(host.Store.Stats, host.Store.Signs);
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains("synthetic-signed-link", StringComparison.Ordinal));
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var asset = await db.AudioAssets.SingleAsync(x => x.Id == (own ?? fallback), Ct);
        Assert.DoesNotContain(asset.ObjectKey, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("en-GB", null, true, "default")]
    [InlineData("en-US", " \t ", true, "default")]
    [InlineData("en-GB", " /uk/ ", false, "default")]
    [InlineData("en-US", "[us]", false, "default")]
    [InlineData("en-GB", "different", false, null)]
    [InlineData("en-US", "different", false, null)]
    [InlineData("en-GB", "different", true, "meaning")]
    [InlineData("en-US", "different", true, "meaning")]
    public async Task AnonymousMeaningSpecificSelection_KeepsMissingAndInheritanceContracts(string locale, string? phonetic, bool hasOwn, string? source)
    {
        await using var host = new Host(); await host.SeedAsync();
        host.Client.DefaultRequestHeaders.Authorization = null;
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var meaning = await db.VocabularyMeanings.SingleAsync(row => row.Id == "m", Ct);
            if (locale == "en-GB") meaning.PhoneticUk = phonetic; else meaning.PhoneticUs = phonetic;
            await db.SaveChangesAsync(Ct);
        }
        var baseline = await host.Bind(locale);
        var own = hasOwn ? await host.Bind(locale, "m") : null;
        using var response = await host.Client.GetAsync(Route(locale, "m"), Ct); using var json = await Read(response);
        var data = Assert.Single(json.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "assetId", "expiresAt", "locale", "source", "sourceRevision", "status", "url" },
            data.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(source, data.GetProperty("source").GetString());
        Assert.Equal(source == null ? "missing" : "ready", data.GetProperty("status").GetString());
        Assert.Equal(source == "meaning" ? own : source == "default" ? baseline : null, data.GetProperty("assetId").GetString());
        Assert.Equal(source == null ? 0 : 1, host.Store.Stats.Count);
        Assert.Equal(host.Store.Stats, host.Store.Signs);
        if (source == null)
            foreach (var key in new[] { "sourceRevision", "url", "expiresAt" }) Assert.Equal(JsonValueKind.Null, data.GetProperty(key).ValueKind);
    }

    [Fact]
    public async Task BothAccentsShareSnapshot_DefaultFallbackAndMissingDoNotCrossAccent()
    {
        await using var host = new Host(); await host.SeedAsync(); var gb = await host.Bind();
        using var response = await host.Client.GetAsync(Route(null, "m"), Ct); using var json = await Read(response);
        var items = json.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length); Assert.Equal("en-GB", items[0].GetProperty("locale").GetString());
        Assert.Equal(gb, items[0].GetProperty("assetId").GetString()); Assert.Equal("default", items[0].GetProperty("source").GetString());
        Assert.Equal("en-US", items[1].GetProperty("locale").GetString()); Assert.Equal("missing", items[1].GetProperty("status").GetString());
        foreach (var key in new[] { "source", "assetId", "sourceRevision", "url", "expiresAt" }) Assert.Equal(JsonValueKind.Null, items[1].GetProperty(key).ValueKind);
        Assert.Single(host.Store.Stats);
    }

    [Theory]
    [InlineData("/api/vocabulary/w/audio", 400)]
    [InlineData("/api/vocabulary/w/audio?bookId=", 400)]
    [InlineData("/api/vocabulary/w/audio?bookId=enabled&locale=en-AU", 400)]
    [InlineData("/api/vocabulary/w/audio?bookId=enabled&locale=", 400)]
    [InlineData("/api/vocabulary/w/audio?bookId=enabled&meaningId=", 400)]
    [InlineData("/api/vocabulary/w/audio?bookId=absent", 404)]
    [InlineData("/api/vocabulary/absent/audio?bookId=enabled", 404)]
    [InlineData("/api/vocabulary/orphan/audio?bookId=enabled", 404)]
    [InlineData("/api/vocabulary/other/audio?bookId=enabled", 404)]
    [InlineData("/api/vocabulary/w/audio?bookId=enabled&meaningId=foreign-m", 404)]
    [InlineData("/api/vocabulary/w/audio?bookId=enabled&meaningId=other-m", 404)]
    [InlineData("/api/vocabulary/w/audio?bookId=disabled", 422)]
    public async Task InvalidScopeNeverContactsStorage(string path, int status)
    {
        await using var host = new Host(); await host.SeedAsync(); await host.Bind();
        using var response = await host.Client.GetAsync(path, Ct); Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(host.Store.Stats); Assert.Empty(host.Store.Signs);
    }

    [Fact]
    public async Task AudioDisabledIsSafe503_AndOldPublicRoutesStillWork()
    {
        await using var host = new Host(false); await host.SeedAsync();
        host.Client.DefaultRequestHeaders.Authorization = null;
        using var response = await host.Client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); Assert.Contains("Audio storage is disabled.", await response.Content.ReadAsStringAsync(Ct));
        using var old = await host.Client.GetAsync("/api/vocabulary-books/enabled/entries", Ct); old.EnsureSuccessStatusCode();
        Assert.DoesNotContain("audio", await old.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase); Assert.Empty(host.Store.Stats);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denial")]
    [InlineData("timeout")]
    [InlineData("sign-failure")]
    public async Task StorageFailureDoesNotFallbackOrLeak(string failure)
    {
        await using var host = new Host(timeout: 1); await host.SeedAsync(); await host.Bind(); await host.Bind(meaning: "m", stale: true);
        const string marker = "synthetic-provider-secret-key-url";
        host.Store.OnStat = failure switch
        {
            "missing" => _ => Task.FromResult<AudioObjectInfo?>(null),
            "denial" => _ => throw new InvalidOperationException(marker),
            "timeout" => async token => { await Task.Delay(Timeout.Infinite, token); return null; }
            ,
            _ => null
        };
        if (failure == "sign-failure") host.Store.OnSign = _ => throw new InvalidOperationException(marker);
        using var response = await host.Client.GetAsync(Route(meaning: "m"), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"success\":false,\"message\":\"Audio storage is unavailable.\"}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Single(host.Store.Stats); Assert.DoesNotContain(host.Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisableDuringNetwork_DoesNotRevokeAuthorizedSnapshot_ThenReenableAndEdit()
    {
        await using var host = new Host(); await host.SeedAsync(); await host.Bind(); await host.Bind("en-US");
        var called = false;
        host.Store.OnStat = async token =>
        {
            if (!called)
            {
                called = true;
                using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=0 WHERE id='enabled'", token).WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            return new AudioObjectInfo(128, "audio/mpeg");
        };
        using var first = await host.Client.GetAsync(Route(null), Ct); using var json = await Read(first);
        Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());
        using var denied = await host.Client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode); Assert.Equal(2, host.Store.Signs.Count);
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=1 WHERE id='enabled'", Ct);
            await scope.ServiceProvider.GetRequiredService<VocabularyWordEditService>().ReplaceAsync("w", "take off", "new-uk", "us", Ct);
        }
        using var stale = await host.Client.GetAsync(Route(), Ct); using var changed = await Read(stale);
        Assert.Equal("stale", changed.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOfStatOrSign_DoesNotHoldSqliteWriteLock(bool signing)
    {
        await using var host = new Host(); await host.SeedAsync(); await host.Bind();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Block(CancellationToken token)
        {
            entered.SetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { stopped.SetResult(); }
        }
        if (signing) host.Store.OnSign = async token => { await Block(token); return null!; };
        else host.Store.OnStat = async token => { await Block(token); return null; };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = host.Client.GetAsync(Route(), cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<VocabularyWordEditService>().ReplaceAsync("w", "take off", "new-uk", "us", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); Assert.Equal(signing ? 1 : 0, host.Store.Signs.Count);
    }

    [Fact]
    public async Task PublicAudioSharesExistingQuota_RetryAfterAndAdminRemainIndependent()
    {
        await using var host = new Host(permits: 1); await host.SeedAsync(); await host.Bind();
        host.Client.DefaultRequestHeaders.Authorization = null;
        using var first = await host.Client.GetAsync(Route(), Ct); first.EnsureSuccessStatusCode();
        using var limited = await host.Client.GetAsync("/api/vocabulary-books/enabled/entries", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After"))) > 0);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Factory.CreateToken("admin"));
        using var admin = await host.Client.GetAsync("/admin/vocabulary/w/audio/en-GB/playback", Ct); admin.EnsureSuccessStatusCode();
    }
}
