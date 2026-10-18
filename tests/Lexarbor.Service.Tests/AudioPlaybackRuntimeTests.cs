using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class AudioPlaybackRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Route(bool publicAudio) => publicAudio
        ? "/api/vocabulary/w/audio?bookId=b" : "/admin/vocabulary/w/audio/en-GB/playback";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitiallyDisabled_EnableCloseAndReopen_OldRequestCompletes(bool publicAudio)
    {
        await using var host = new Host(enabled: false);
        await host.Seed();
        using (var disabled = await host.Client.GetAsync(Route(publicAudio), Ct))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        await host.Change(options => options.Enabled = true);
        var old = host.Stores.Created.Last();
        var entered = Signal(); var finish = Signal();
        old.Sign = async token => { entered.TrySetResult(); await finish.Task.WaitAsync(token); };
        var pending = host.Client.GetAsync(Route(publicAudio), Ct);
        await entered.Task.WaitAsync(Ct);
        await host.Change(options => options.Enabled = false);
        Assert.Equal(0, old.Disposed);
        using (var disabled = await host.Client.GetAsync(Route(publicAudio), Ct))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        finish.SetResult();
        using (var completed = await pending) await Verify(completed, old, publicAudio);
        await Eventually(() => old.Disposed == 1);
        await host.Change(options => options.Enabled = true);
        var current = host.Stores.Created.Last();
        using var reopened = await host.Client.GetAsync(Route(publicAudio), Ct);
        await Verify(reopened, current, publicAudio);
        Assert.Equal(publicAudio ? 2 : 1, current.Signs.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RotationDuringStatOrSignBoundary_AllAccentsAndNewRequestsUseTheirOwnGeneration(bool publicAudio, bool signing)
    {
        await using var host = new Host(); await host.Seed();
        var old = host.Stores.Created.Single();
        var entered = Signal(); var finish = Signal(); var calls = 0;
        async Task Pause(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) != (publicAudio && signing ? 2 : 1)) return;
            entered.SetResult(); await finish.Task.WaitAsync(token);
        }
        if (signing) old.Sign = Pause; else old.Stat = Pause;
        var pending = host.Client.GetAsync(Route(publicAudio), Ct);
        await entered.Task.WaitAsync(Ct);
        await host.Change(options =>
        {
            options.AccessKey = "synthetic-rotated-access"; options.SecretKey = "synthetic-rotated-secret";
            options.PublicEndpoint = "https://new.example.test/media/";
        });
        Assert.Equal(0, old.Disposed);
        var current = host.Stores.Created.Last();
        using (var next = await host.Client.GetAsync(Route(publicAudio), Ct)) await Verify(next, current, publicAudio);
        Assert.Equal(publicAudio ? 2 : 1, current.Signs.Count);
        Assert.Equal("synthetic-rotated-access", current.Options.AccessKey);
        Assert.Equal("synthetic-playback-access", old.Options.AccessKey);
        // A write on another scope completes while storage and the old read are paused.
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Null(db.Database.CurrentTransaction);
            db.VocabularyBooks.Add(new() { Id = "concurrent", BookName = "Concurrent", Status = true });
            await db.SaveChangesAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finish.SetResult();
        using (var completed = await pending) await Verify(completed, old, publicAudio);
        Assert.Equal(publicAudio ? 2 : 1, old.Signs.Count);
        Assert.All(old.Signs, validity => Assert.Equal(TimeSpan.FromSeconds(120), validity));
        await Eventually(() => old.Disposed == 1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CancelOrTimeout_WithUncooperativeProvider_HoldsClientUntilActualEnd(bool publicAudio, bool signing, bool cancel)
    {
        await using var host = new Host(timeout: 1); await host.Seed();
        var old = host.Stores.Created.Single();
        var entered = Signal(); var finish = Signal();
        async Task Pause(CancellationToken _) { entered.SetResult(); await finish.Task; }
        if (signing) old.Sign = Pause; else old.Stat = Pause;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = host.Client.GetAsync(Route(publicAudio), cancellation.Token);
        await entered.Task.WaitAsync(Ct);
        await host.Change(options => options.Enabled = false);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            using var response = await pending;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("{\"success\":false,\"message\":\"Audio storage is unavailable.\"}", await response.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal(0, old.Disposed);
        Assert.Equal(signing ? 1 : 0, old.Signs.Count);
        finish.SetResult();
        await Eventually(() => old.Disposed == 1);
        Assert.Equal(signing ? 1 : 0, old.Signs.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationReservation_WaitsAsynchronouslyAndCancellationDoesNotAcquire(bool publicAudio)
    {
        await using var host = new Host(enabled: false);
        // No records: a destination preparation may reserve the acquisition gate.
        var entered = Signal(); var finish = Signal();
        host.Records.Check = async token => { entered.SetResult(); await finish.Task.WaitAsync(token); return false; };
        var options = host.Runtime.CopyCurrentOptions(); options.Bucket = "next-bucket"; options.Enabled = true;
        var preparation = host.Runtime.PrepareAsync(Config(options), Ct);
        await entered.Task.WaitAsync(Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = host.Client.GetAsync(Route(publicAudio), cancellation.Token);
        var waiting = host.Client.GetAsync(Route(publicAudio), Ct);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        // Normal HTTP requests still complete while acquisition waits.
        using var health = await host.Client.GetAsync("/health", Ct); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await host.Seed();
        finish.SetResult(); using var prepared = await preparation; Assert.NotNull(prepared); prepared.Publish();
        using var result = await waiting;
        await Verify(result, host.Stores.Created.Last(), publicAudio);
        Assert.Equal(1, host.Stores.Created.First().Disposed);
        using var next = await host.Client.GetAsync(Route(publicAudio), Ct);
        await Verify(next, host.Stores.Created.Last(), publicAudio);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("construct")]
    [InlineData("write")]
    [InlineData("replace")]
    public async Task FailedCandidateOrFileCommit_PreservesOldPlaybackAndOrdinaryApis(string failure)
    {
        await using var host = new Host(); await host.Seed();
        var old = host.Stores.Created.Single();
        var root = Path.Combine(AppContext.BaseDirectory, "lexarbor-playback-282-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "data", "appsettings.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject { ["Audio"] = JsonSerializer.SerializeToNode(old.Options) }.ToJsonString());
            var before = File.ReadAllBytes(path);
            var coordinator = new PersistentConfigurationCoordinator(new ConfigurationBuilder().AddJsonFile(path).Build(), new FailingFileStore(root, failure));
            host.Stores.Fail = failure == "construct";
            var fields = new Dictionary<string, JsonNode?> { ["Audio:PublicEndpoint"] = failure == "invalid" ? "invalid" : "https://new.example.test/media/" };
            var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, fields, host.Runtime.PrepareAsync, Ct);
            Assert.Equal(failure is "write" or "replace" ? ConfigurationSaveStatus.StorageFailure : ConfigurationSaveStatus.InvalidCandidate, result.Status);
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(0, old.Disposed);
            foreach (var publicAudio in new[] { false, true })
            {
                using var response = await host.Client.GetAsync(Route(publicAudio), Ct); await Verify(response, old, publicAudio);
            }
            old.Stat = _ => throw new InvalidOperationException("synthetic-provider-secret-complete-signed-url");
            using var offline = await host.Client.GetAsync(Route(true), Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
            Assert.DoesNotContain("synthetic-provider", await offline.Content.ReadAsStringAsync(Ct));
            using var health = await host.Client.GetAsync("/health", Ct); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var ordinary = await host.Client.GetAsync("/api/vocabulary?bookId=b&keyword=tone", Ct); ordinary.EnsureSuccessStatusCode();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string TextbookRoute(bool publicTrack) => publicTrack ? "/api/vocabulary-books/b/units/unit/audio-tracks/track/playback" : TextbookRouteAdmin;
    private const string TextbookRouteAdmin = "/admin/vocabulary-books/b/units/unit/audio-tracks/track/playback";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Textbook_RotationOrCloseDuringStatOrSign_HoldsCapturedGeneration(bool signing, bool close, bool publicTrack)
    {
        await using var host = new Host(); await host.Seed(); var old = host.Stores.Created.Single();
        var entered = Signal(); var finish = Signal();
        async Task Pause(CancellationToken ct) { entered.SetResult(); await finish.Task.WaitAsync(ct); }
        if (signing) old.Sign = Pause; else old.Stat = Pause;
        var pending = host.Client.GetAsync(TextbookRoute(publicTrack), Ct); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await host.Change(options => { options.Enabled = !close; options.AccessKey = "synthetic-rotated-access"; options.SecretKey = "synthetic-rotated-secret"; options.PublicEndpoint = "https://new.example.test/"; });
        Assert.Equal(0, old.Disposed);
        using (var next = await host.Client.GetAsync(TextbookRoute(publicTrack), Ct))
        {
            Assert.Equal(close ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, next.StatusCode);
            if (!close) await Verify(next, host.Stores.Created.Last(), false);
        }
        finish.SetResult(); using (var completed = await pending) await Verify(completed, old, false);
        Assert.Single(old.Signs); await Eventually(() => old.Disposed == 1);
        if (close)
        {
            await host.Change(options => options.Enabled = true);
            using var reopened = await host.Client.GetAsync(TextbookRoute(publicTrack), Ct); await Verify(reopened, host.Stores.Created.Last(), false);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Textbook_CancelOrDeadlineWithLateProvider_RetainsClientUntilActualEnd(bool signing, bool cancel, bool publicTrack)
    {
        await using var host = new Host(timeout: 1); await host.Seed(); var old = host.Stores.Created.Single();
        var entered = Signal(); var finish = Signal();
        async Task Pause(CancellationToken _) { entered.SetResult(); await finish.Task; }
        if (signing) old.Sign = Pause; else old.Stat = Pause;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = host.Client.GetAsync(TextbookRoute(publicTrack), cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await host.Change(options => options.Enabled = false);
        if (cancel) { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        else { using var response = await pending; Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); }
        Assert.Equal(0, old.Disposed); Assert.Equal(signing ? 1 : 0, old.Signs.Count);
        finish.SetResult(); await Eventually(() => old.Disposed == 1); Assert.Equal(signing ? 1 : 0, old.Signs.Count);
    }

    private static async Task Verify(HttpResponseMessage response, Store store, bool publicAudio)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var data = json.RootElement.GetProperty("data");
        foreach (var item in publicAudio ? data.GetProperty("items").EnumerateArray().ToArray() : new[] { data })
        {
            Assert.StartsWith(store.Options.PublicEndpoint + "synthetic-signed-", item.GetProperty("url").GetString());
            Assert.Equal(TimeSpan.Zero, item.GetProperty("expiresAt").GetDateTimeOffset().Offset);
        }
        if (publicAudio) Assert.Equal(2, data.GetProperty("items").GetArrayLength());
    }
    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static IConfigurationRoot Config(AudioOptions options) => new ConfigurationBuilder()
        .AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }))).Build();
    private sealed class FailingFileStore(string root, string failure) : PersistentConfigurationFileStore(root)
    {
        protected override async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken token)
        { await base.WriteTemporaryAsync(path, bytes, token); if (failure == "write") throw new IOException("synthetic-file-secret"); }
        protected override void Replace(string path, bool overwrite)
        { if (failure == "replace") throw new IOException("synthetic-file-secret"); base.Replace(path, overwrite); }
    }
    private sealed class Records : IAudioDestinationRecords
    {
        public Func<CancellationToken, Task<bool>> Check = _ => Task.FromResult(false);
        public Task<bool> HasAnyAsync(CancellationToken token) => Check(token);
    }
    private sealed class Stores : IAudioObjectStoreFactory
    {
        public readonly List<Store> Created = [];
        public bool Fail;
        public IAudioObjectStore Create(AudioOptions options)
        {
            if (Fail) throw new InvalidOperationException("synthetic-client-secret");
            var store = new Store(options); Created.Add(store); return store;
        }
    }
    private sealed class Store(AudioOptions options) : IAudioObjectStore, IDisposable
    {
        public AudioOptions Options { get; } = options;
        public Func<CancellationToken, Task>? Stat;
        public Func<CancellationToken, Task>? Sign;
        public readonly List<TimeSpan> Signs = [];
        public int Disposed;
        public async Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token)
        { Assert.Equal(0, Disposed); if (Stat != null) await Stat(token); Assert.Equal(0, Disposed); return new(128, "audio/mpeg"); }
        public async Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token)
        {
            Assert.Equal(0, Disposed); Signs.Add(validity); if (Sign != null) await Sign(token); Assert.Equal(0, Disposed);
            return new(Options.PublicEndpoint + "synthetic-signed-" + Signs.Count, DateTimeOffset.UtcNow.Add(validity));
        }
        public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token)
            => PutAsync(location, content, length, type, AudioUploadPurpose.Vocabulary, token);
        public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, AudioUploadPurpose purpose, CancellationToken token) => throw new NotSupportedException();
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly Stores Stores = new();
        public readonly Records Records = new();
        public readonly VocabularyWebApplicationFactory Factory;
        public readonly WebApplicationFactory<Program> App;
        public readonly HttpClient Client;
        public AudioRuntimeConfiguration Runtime => App.Services.GetRequiredService<AudioRuntimeConfiguration>();
        public Host(bool enabled = true, int timeout = 60)
        {
            Factory = new("Testing", true, extraConfiguration: new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = enabled.ToString(),
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:PublicEndpoint"] = "https://old.example.test/s3/",
                ["Audio:AccessKey"] = "synthetic-playback-access",
                ["Audio:SecretKey"] = "synthetic-playback-secret",
                ["Audio:PlaybackTtlSeconds"] = "120",
                ["Audio:OperationTimeoutSeconds"] = timeout.ToString()
            });
            App = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(Stores);
                services.RemoveAll<IAudioDestinationRecords>(); services.AddSingleton<IAudioDestinationRecords>(Records);
            }));
            Client = App.CreateClient(new() { BaseAddress = new("https://localhost") });
            Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        public async Task Change(Action<AudioOptions> change)
        {
            var options = Runtime.CopyCurrentOptions(); change(options);
            using var prepared = await Runtime.PrepareAsync(Config(options), Ct); Assert.NotNull(prepared); prepared.Publish();
        }
        public async Task Seed()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.Add(new() { Id = "w", Word = "tone" });
            db.VocabularyBooks.Add(new() { Id = "b", BookName = "Book", Status = true });
            db.VocabularyMeanings.Add(new() { Id = "m", VocabularyId = "w", BookId = "b", Meaning = "tone" });
            await db.SaveChangesAsync(Ct);
            var snapshot = await scope.ServiceProvider.GetRequiredService<IAudioReadRepository>().GetSnapshotAsync("w", null, Ct);
            foreach (var locale in Enum.GetValues<AudioLocale>())
            {
                var id = Guid.NewGuid().ToString("N");
                db.AudioAssets.Add(new AudioAssetEntity
                {
                    Id = id,
                    Bucket = Runtime.CopyCurrentOptions().Bucket,
                    ObjectKey = "audio/" + id + ".mp3",
                    Sha256 = new string('a', 64),
                    ByteLength = 128,
                    CreatedAtUnixMs = 1,
                    FirstPublishedAtUnixMs = 2
                });
                db.VocabularyAudioBindings.Add(new VocabularyAudioBindingEntity
                {
                    Id = id,
                    WordId = "w",
                    Locale = locale,
                    AssetId = id,
                    SourceRevision = AudioSelectionService.Select(snapshot, locale).SourceRevision
                });
            }
            await db.SaveChangesAsync(Ct);
            db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "b", Number = 1 });
            db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "b", UnitId = "unit", Title = "Recording", IsAvailable = true, CurrentAssetId = (await db.AudioAssets.AsNoTracking().FirstAsync(Ct)).Id, DurationSeconds = 1, Version = Guid.NewGuid().ToString("D") });
            await db.SaveChangesAsync(Ct);

        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
}
