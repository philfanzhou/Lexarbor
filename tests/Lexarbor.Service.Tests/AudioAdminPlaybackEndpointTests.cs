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

public sealed class AudioAdminPlaybackEndpointTests
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
        public async Task SeedAsync()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.AddRange(new() { Id = "w", Word = "take off", PhoneticUk = "uk", PhoneticUs = "us" },
                new() { Id = "orphan", Word = "orphan" }, new() { Id = "other", Word = "other" });
            db.VocabularyBooks.Add(new() { Id = "disabled", BookName = "Disabled", Status = false });
            db.VocabularyMeanings.AddRange(new() { Id = "m", VocabularyId = "w", BookId = "disabled", PartOfSpeech = "v.", Meaning = "leave", PhoneticUk = "uk", PhoneticUs = "us" },
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
    private static string Route(string locale = "en-GB", string? meaning = null, string word = "w")
        => "/admin/vocabulary/" + word + "/audio/" + locale + "/playback" + (meaning == null ? "" : "?meaningId=" + meaning);
    private static async Task<JsonDocument> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("en-GB", null, false)]
    [InlineData("en-US", null, true)]
    [InlineData("en-GB", "m", false)]
    [InlineData("en-US", "m", true)]
    public async Task SelectedOwnOrDefaultBinding_IncludingStale_UsesOnlySameAccent(string locale, string? meaning, bool stale)
    {
        await using var host = new Host(ttl: 120); await host.SeedAsync();
        var gb = await host.Bind(); var us = await host.Bind("en-US", stale: meaning == null && stale);
        var own = meaning == null ? null : await host.Bind(locale, meaning, stale);
        using var response = await host.Client.GetAsync(Route(locale, meaning), Ct); using var json = await Read(response);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(locale, data.GetProperty("locale").GetString()); Assert.Equal(stale ? "stale" : "ready", data.GetProperty("status").GetString());
        Assert.Equal(meaning == null ? "default" : "meaning", data.GetProperty("source").GetString());
        Assert.Equal(own ?? (locale == "en-GB" ? gb : us), data.GetProperty("assetId").GetString());
        Assert.Equal("https://audio.example.test/synthetic-signed-link", data.GetProperty("url").GetString());
        Assert.Equal(TimeSpan.Zero, data.GetProperty("expiresAt").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.FromSeconds(120), host.Store.Validity); Assert.Single(host.Store.Stats); Assert.Equal(host.Store.Stats, host.Store.Signs);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var selected = await db.AudioAssets.SingleAsync(x => x.Id == data.GetProperty("assetId").GetString(), Ct);
        Assert.Equal(selected.ObjectKey, host.Store.Stats[0].Key);
        Assert.DoesNotContain(selected.ObjectKey, await response.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains("synthetic-signed-link", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains(selected.ObjectKey, StringComparison.Ordinal));
        Assert.Empty(await db.AudioUploadOperations.ToListAsync(Ct));
    }

    [Fact]
    public async Task MeaningFallbackAndOrphan_AreAllowed_AndMissingNeverCrossesAccent()
    {
        await using var host = new Host(); await host.SeedAsync(); var gb = await host.Bind();
        using var fallback = await host.Client.GetAsync(Route(meaning: "m"), Ct); using var json = await Read(fallback);
        Assert.Equal("default", json.RootElement.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal(gb, json.RootElement.GetProperty("data").GetProperty("assetId").GetString());
        using var missing = await host.Client.GetAsync(Route("en-US", "m"), Ct); using var absent = await Read(missing);
        var data = absent.RootElement.GetProperty("data"); Assert.Equal("missing", data.GetProperty("status").GetString());
        foreach (var key in new[] { "source", "assetId", "sourceRevision", "url", "expiresAt" }) Assert.Equal(JsonValueKind.Null, data.GetProperty(key).ValueKind);
        var orphan = await host.Bind(word: "orphan");
        using var orphanResponse = await host.Client.GetAsync(Route(word: "orphan"), Ct); using var orphanJson = await Read(orphanResponse);
        Assert.Equal(orphan, orphanJson.RootElement.GetProperty("data").GetProperty("assetId").GetString()); Assert.Equal(2, host.Store.Stats.Count);
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
    public async Task MeaningSpecificSelection_ControlsPlaybackWithoutChangingResponseFields(string locale, string? phonetic, bool hasOwn, string? source)
    {
        await using var host = new Host(); await host.SeedAsync();
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
        var data = json.RootElement.GetProperty("data");
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

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("student-cookie", 403)]
    [InlineData("invalid-bearer-cookie", 401)]
    public async Task IdentityPrecedesInvalidInput_AndBearerPrecedesCookie(string identity, int status)
    {
        await using var host = new Host(); await host.SeedAsync();
        host.Client.DefaultRequestHeaders.Authorization = identity == "anonymous" ? null
            : identity == "invalid-bearer-cookie" ? new("Bearer", "invalid") : new("Bearer", host.Factory.CreateToken("student"));
        if (identity.EndsWith("cookie", StringComparison.Ordinal)) host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("admin"));
        using var response = await host.Client.GetAsync(Route("invalid", "wrong"), Ct); Assert.Equal(status, (int)response.StatusCode); Assert.Empty(host.Store.Stats);
    }

    [Fact]
    public async Task CookieGetNeedsNoCsrf_AndPlaybackKeepsExistingRateLimitPolicies()
    {
        await using var host = new Host(); await host.SeedAsync(); await host.Bind();
        host.Client.DefaultRequestHeaders.Authorization = null; host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("admin"));
        for (var i = 0; i < 5; i++) { using var response = await host.Client.GetAsync(Route(), Ct); response.EnsureSuccessStatusCode(); }
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Factory.CreateToken("admin"));
        using var bearerWithCookie = await host.Client.GetAsync(Route(), Ct); bearerWithCookie.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("/admin/vocabulary/w/audio/en-AU/playback", 400)]
    [InlineData("/admin/vocabulary/w/audio/en-GB/playback?meaningId=", 400)]
    [InlineData("/admin/vocabulary/w/audio/en-GB/playback?meaningId=other-m", 404)]
    [InlineData("/admin/vocabulary/w/audio/en-GB/playback?meaningId=absent", 404)]
    [InlineData("/admin/vocabulary/absent/audio/en-GB/playback", 404)]
    public async Task BadInputsAndWrongOwnership_DoNotContactStorage(string path, int status)
    {
        await using var host = new Host(); await host.SeedAsync(); await host.Bind();
        using var response = await host.Client.GetAsync(path, Ct); Assert.Equal(status, (int)response.StatusCode); Assert.Empty(host.Store.Stats); Assert.Empty(host.Store.Signs);
    }

    [Fact]
    public async Task DisabledAudio_ReturnsSafe503WithoutStorage_IdentityStillRequired()
    {
        await using var host = new Host(false); await host.SeedAsync();
        using var response = await host.Client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); Assert.Contains("Audio storage is disabled.", await response.Content.ReadAsStringAsync(Ct));
        host.Client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await host.Client.GetAsync(Route(), Ct); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); Assert.Empty(host.Store.Stats);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denial")]
    [InlineData("timeout")]
    [InlineData("sign-failure")]
    public async Task SelectedOverrideFailure_IsFixed503WithoutFallbackOrLeakingProviderValues(string failure)
    {
        await using var host = new Host(timeout: 1); await host.SeedAsync(); await host.Bind(); var own = await host.Bind(meaning: "m", stale: true);
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
        using var response = await host.Client.GetAsync(Route(meaning: "m"), Ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"success\":false,\"message\":\"Audio storage is unavailable.\"}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); Assert.Single(host.Store.Stats);
        Assert.Equal(failure == "sign-failure" ? 1 : 0, host.Store.Signs.Count);
        Assert.DoesNotContain(host.Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(own, (await db.VocabularyAudioBindings.SingleAsync(x => x.MeaningId == "m", Ct)).AssetId);
    }

    [Fact]
    public async Task ReplacementDuringStat_DoesNotChangeTheSelectedSnapshotOrHoldAReadTransaction()
    {
        await using var host = new Host(); await host.SeedAsync(); var first = await host.Bind();
        string? replacement = null;
        host.Store.OnStat = async token =>
        {
            using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            replacement = Guid.NewGuid().ToString();
            db.AudioAssets.Add(new AudioAssetEntity
            {
                Id = replacement,
                Bucket = "lexarbor-audio",
                ObjectKey = "audio/" + Guid.NewGuid().ToString("N") + ".mp3",
                Sha256 = new string('c', 64),
                ByteLength = 128,
                CreatedAtUnixMs = 3,
                FirstPublishedAtUnixMs = 4
            });
            (await db.VocabularyAudioBindings.SingleAsync(token)).AssetId = replacement;
            await db.SaveChangesAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            return new AudioObjectInfo(128, "audio/mpeg");
        };
        using var response = await host.Client.GetAsync(Route(), Ct); using var json = await Read(response);
        Assert.Equal(first, json.RootElement.GetProperty("data").GetProperty("assetId").GetString());
        Assert.Equal(host.Store.Stats, host.Store.Signs);
        using var finalScope = host.App.Services.CreateScope(); var finalDb = finalScope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(replacement, (await finalDb.VocabularyAudioBindings.SingleAsync(Ct)).AssetId);
        Assert.Equal(2, await finalDb.AudioAssets.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsStatOrSigning_AndBlockedNetworkDoesNotBlockWrites(bool signing)
    {
        await using var host = new Host(); await host.SeedAsync(); var asset = await host.Bind();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Block(CancellationToken token)
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.SetResult(); }
        }
        if (signing) host.Store.OnSign = async token => { await Block(token); return null!; };
        else host.Store.OnStat = async token => { await Block(token); return null; };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = host.Client.GetAsync(Route(), cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>(); Assert.Null(db.Database.CurrentTransaction);
            var edit = scope.ServiceProvider.GetRequiredService<VocabularyWordEditService>().ReplaceAsync("w", "take off", "new-uk", "us", Ct);
            await edit.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(asset, (await db.VocabularyAudioBindings.SingleAsync(Ct)).AssetId);
        }
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(signing ? 1 : 0, host.Store.Signs.Count);
    }
}
