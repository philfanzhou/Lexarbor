using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class AudioRecoveryWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DisabledStartup_EnableDisableReenable_ResumesOneBoundedWorkerAndStopsWaiting()
    {
        using var f = new Fixture(enabled: false);
        await f.Worker.StartAsync(Ct);
        await f.Change(o => o.Enabled = false); // An additional disabled notification is harmless.
        Assert.Equal(0, f.Batches); Assert.False(f.Worker.ExecuteTask!.IsCompleted);
        await f.Change(o => o.Enabled = true);
        await f.Batch.Reader.ReadAsync(Ct);
        await f.Clock.Created.Reader.ReadAsync(Ct); // The batch completed and started its interval.
        Assert.Equal(1, f.Batches);
        await f.Change(o => o.Enabled = true);
        // Enabled notifications cannot skip the existing interval or spawn another worker.
        f.Clock.Fire();
        await f.Batch.Reader.ReadAsync(Ct); await f.Clock.Created.Reader.ReadAsync(Ct);
        Assert.Equal(2, f.Batches);
        await f.Change(o => o.Enabled = false);
        f.Clock.Fire();
        // Wait for the runtime observer to see the disabled generation, then re-enable.
        await f.Change(o => o.Enabled = true);
        await f.Batch.Reader.ReadAsync(Ct);
        Assert.Equal(3, f.Batches);
        await f.Change(o => o.Enabled = false);
        await f.Worker.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.True(f.Worker.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisabledWorker_DoesNotResolveRepositoryOrStorage_AndHostStopCancelsWait()
    {
        using var f = new Fixture(enabled: false);
        await f.Worker.StartAsync(Ct);
        await f.Change(o => o.Enabled = false);
        Assert.False(f.Worker.ExecuteTask!.IsCompleted);
        await f.Worker.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(0, f.Batches); Assert.All(f.Stores.Created, s => Assert.Empty(s.Calls));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RotationAndDisableDuringRemoveOrStat_EntireBatchKeepsOldClient(bool stat, bool mixed)
    {
        using var f = new Fixture(candidates: 2, mixed: mixed);
        var old = f.Stores.Created[0]; old.PauseAt = stat ? "stat" : "remove";
        await f.Worker.StartAsync(Ct); await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await f.Change(o => { o.SecretKey = "synthetic-rotated-secret"; o.PublicEndpoint = "https://new.example.test/"; o.Enabled = false; });
        Assert.Equal(0, old.Disposed);
        old.Finish.TrySetResult();
        await old.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(new[] { "remove", "stat", "remove", "stat" }, old.Calls);
        Assert.All(f.Stores.Created.Skip(1), s => Assert.Empty(s.Calls));
        Assert.Equal(1, f.Batches); Assert.Equal(2, f.Observed);
        Assert.Equal("synthetic-rotated-secret", f.Stores.Created[1].Options.SecretKey);
        await f.Clock.Created.Reader.ReadAsync(Ct);
        await f.Change(o => o.Enabled = true);
        await f.Stores.Created[2].Called.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await f.Clock.Created.Reader.ReadAsync(Ct);
        Assert.Equal(new[] { "remove", "stat", "remove", "stat" }, f.Stores.Created[2].Calls);
        Assert.Equal(4, f.Observed); Assert.Equal(2, f.Batches);
        await f.Worker.StopAsync(Ct);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task UncooperativeRemoveOrStat_TimeoutOrShutdownRetainsClientUntilActualCompletion(bool stat, bool timeout, bool textbook)
    {
        using var f = new Fixture(candidates: 1, timeout: 1, textbook: textbook);
        var old = f.Stores.Created[0]; old.PauseAt = stat ? "stat" : "remove";
        await f.Worker.StartAsync(Ct); await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await f.Change(o => { o.SecretKey = "synthetic-rotated-secret"; o.Enabled = false; });
        if (timeout) await f.Released.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await f.Worker.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(0, old.Disposed); Assert.Equal(0, f.Observed);
        old.Finish.TrySetResult(); await old.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(1, old.Disposed);
        Assert.Equal(stat ? new[] { "remove", "stat" } : new[] { "remove" }, old.Calls);
        Assert.Equal(timeout ? 1 : 0, f.Failures);
    }

    [Fact]
    public async Task BatchFailure_PreservesIntervalAndLogsNoProviderDetails()
    {
        using var f = new Fixture(); f.RepositoryFailure = true;
        await f.Worker.StartAsync(Ct); await f.Batch.Reader.ReadAsync(Ct); await f.Clock.Created.Reader.ReadAsync(Ct);
        Assert.Equal(1, f.Batches); Assert.False(f.Worker.ExecuteTask!.IsCompleted);
        f.Clock.Fire(); await f.Batch.Reader.ReadAsync(Ct); await f.Clock.Created.Reader.ReadAsync(Ct);
        Assert.Equal(2, f.Batches);
        await f.Worker.StopAsync(Ct);
        Assert.All(f.Logger.Invocations, call => { Assert.Null(call.Arguments[3]); Assert.DoesNotContain("synthetic-provider-secret", call.Arguments[2]?.ToString() ?? ""); });
    }

    [Fact]
    public async Task FailedCandidateAndDiscard_DoNotWakeDisabledWorkerOrReplaceSnapshot()
    {
        using var f = new Fixture(enabled: false); await f.Worker.StartAsync(Ct);
        using var initial = await f.Runtime.AcquireAsync(Ct);
        var invalid = initial.Snapshot.Generation;
        var options = f.Runtime.CopyCurrentOptions(); options.Enabled = true; options.SecretKey = "";
        using var config = Config(options); Assert.Null(await f.Runtime.PrepareAsync(config, Ct));
        options.SecretKey = "synthetic-candidate-secret";
        using var valid = Config(options); using (var discard = await f.Runtime.PrepareAsync(valid, Ct)) Assert.NotNull(discard);
        using var current = await f.Runtime.AcquireAsync(Ct);
        Assert.Equal(invalid, current.Snapshot.Generation); Assert.Equal(0, f.Batches);
        Assert.Equal(1, f.Stores.Created.Last().Disposed);
        await f.Worker.StopAsync(Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileWriteOrReplaceFailure_DoesNotEnableWorkerOrChangePersistedConfiguration(bool replace)
    {
        using var f = new Fixture(enabled: false);
        var root = Path.Combine(AppContext.BaseDirectory, "audio-recovery-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var path = Path.Combine(root, "data", "appsettings.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Audio = f.Runtime.CopyCurrentOptions() });
        await File.WriteAllBytesAsync(path, bytes, Ct);
        try
        {
            using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(path).Build();
            var coordinator = new PersistentConfigurationCoordinator(configuration, new FailedFileStore(root, replace));
            await f.Worker.StartAsync(Ct);
            var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision,
                new Dictionary<string, JsonNode?> { ["Audio:Enabled"] = JsonValue.Create(true) }, f.Runtime.PrepareAsync, Ct);
            Assert.Equal(ConfigurationSaveStatus.StorageFailure, result.Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct)); Assert.False(f.Runtime.CopyCurrentOptions().Enabled);
            Assert.Equal(0, f.Batches); Assert.Equal(1, f.Stores.Created.Last().Disposed);
            Assert.False(f.Worker.ExecuteTask!.IsCompleted); await f.Worker.StopAsync(Ct);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SustainedMixedCandidates_AlternateFirstTypeAcrossFreshBatchScopes(int batchSize)
    {
        using var f = new Fixture(candidates: 8, mixed: true, batchSize: batchSize);
        await f.Worker.StartAsync(Ct);
        await f.Clock.Created.Reader.ReadAsync(Ct);
        Assert.Equal(batchSize, f.Claims.Count);
        Assert.Equal("vocabulary", f.Claims[0]);
        f.Clock.Fire(); await f.Clock.Created.Reader.ReadAsync(Ct);
        Assert.Equal(2 * batchSize, f.Claims.Count);
        Assert.Equal("textbook", f.Claims[batchSize]);
        Assert.Equal(2, f.Scopes.Count);
        for (var i = 0; i < batchSize; i++)
        {
            Assert.Equal(i % 2 == 0 ? "vocabulary" : "textbook", f.Claims[i]);
            Assert.Equal(i % 2 == 0 ? "textbook" : "vocabulary", f.Claims[batchSize + i]);
        }
        await f.Worker.StopAsync(Ct);
    }

    private sealed class FailedFileStore(string root, bool replace) : PersistentConfigurationFileStore(root)
    {
        protected override async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken token)
        { await base.WriteTemporaryAsync(path, bytes, token); if (!replace) throw new IOException("synthetic-file-secret"); }
        protected override void Replace(string path, bool overwrite) => throw new IOException("synthetic-replace-secret");
    }

    [Fact]
    public async Task HostStop_CancelsAcquisitionDuringPendingDestinationPreparation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new Mock<IAudioDestinationRecords>();
        records.Setup(r => r.HasAnyAsync(It.IsAny<CancellationToken>())).Returns(() => { entered.TrySetResult(); return finish.Task; });
        var options = AudioStorageTests.ValidOptions(); options.Enabled = false;
        using var runtime = new AudioRuntimeConfiguration(Options.Create(options), new Stores(), records.Object);
        options.Bucket = "synthetic-new-bucket"; using var configuration = Config(options);
        var preparing = runtime.PrepareAsync(configuration, Ct); await entered.Task.WaitAsync(Ct);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var worker = new AudioRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), runtime,
            TimeProvider.System, Mock.Of<ILogger<AudioRecoveryWorker>>());
        await worker.StartAsync(Ct); await worker.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.True(worker.ExecuteTask!.IsCompleted); Assert.False(worker.ExecuteTask.IsFaulted);
        finish.TrySetResult(false); using var prepared = await preparing; Assert.NotNull(prepared);
    }

    [Fact]
    public async Task PublicationBeforeSubscription_IsNotLost_AndCancelledObserverDoesNotConsumeNotification()
    {
        using var f = new Fixture(enabled: false); using var initial = await f.Runtime.AcquireAsync(Ct);
        using var cancel = new CancellationTokenSource();
        var observer = f.Runtime.WaitForChangeAsync(initial.Snapshot.Generation, cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
        await f.Change(o => o.Enabled = true);
        Assert.True((await f.Runtime.WaitForChangeAsync(initial.Snapshot.Generation, Ct)).Enabled);
    }

    [Fact]
    public async Task DisableReenableBeforeNotificationSubscription_WakesButRepeatedEnableDoesNot()
    {
        using var f = new Fixture(); using var initial = await f.Runtime.AcquireAsync(Ct);
        await f.Change(o => o.Enabled = false); await f.Change(o => o.Enabled = true);
        Assert.True((await f.Runtime.WaitForEnableAsync(initial.Snapshot.Generation, Ct)).Enabled);
        using var enabled = await f.Runtime.AcquireAsync(Ct);
        await f.Change(o => o.Enabled = true);
        using var cancellation = new CancellationTokenSource();
        var wait = f.Runtime.WaitForEnableAsync(enabled.Snapshot.Generation, cancellation.Token);
        Assert.False(wait.IsCompleted); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    private static ConfigurationRoot Config(AudioOptions options) => (ConfigurationRoot)new ConfigurationBuilder()
        .AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }))).Build();

    private sealed class Fixture : IDisposable
    {
        public readonly Stores Stores = new(); public readonly Clock Clock = new(); public readonly Mock<ILogger<AudioRecoveryWorker>> Logger = new();
        public readonly Channel<int> Batch = Channel.CreateUnbounded<int>(); public readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly AudioRuntimeConfiguration Runtime; public readonly AudioRecoveryWorker Worker; private readonly ServiceProvider _provider;
        public int Batches; public int Observed; public int Failures; public bool RepositoryFailure;
        public readonly List<string> Claims = []; public readonly HashSet<Guid> Scopes = [];
        public Fixture(bool enabled = true, int candidates = 0, int timeout = 3, bool textbook = false, bool mixed = false, int batchSize = 2)
        {
            var options = AudioStorageTests.ValidOptions(); options.Enabled = enabled; options.OperationTimeoutSeconds = timeout;
            options.RecoveryBatchSize = batchSize; options.RecoveryLeaseSeconds = 40; options.RecoveryRetrySeconds = 17; options.RecoveryRecheckSeconds = 4000;
            Runtime = new(Options.Create(options), Stores);
            var uploads = Enumerable.Range(0, textbook ? 0 : mixed ? candidates / 2 : candidates).Select(_ => Upload(options.Bucket)).ToArray();
            var repo = new Mock<IAudioUploadRepository>();
            repo.Setup(r => r.FindRecoverableAsync(It.IsAny<long>(), batchSize, It.IsAny<CancellationToken>())).Returns(() =>
            {
                var count = Interlocked.Increment(ref Batches); Batch.Writer.TryWrite(count);
                if (RepositoryFailure) throw new InvalidOperationException("synthetic-provider-secret");
                return Task.FromResult<IReadOnlyList<AudioStoredUpload>>(uploads);
            });
            repo.Setup(r => r.ClaimCleanupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string key, string owner, long now, long expiry, CancellationToken ct) =>
                { Claims.Add("vocabulary"); Assert.Equal(now + 40000, expiry); return Task.FromResult<AudioStoredUpload?>(uploads.Single(u => u.Operation.IdempotencyKey == key)); });
            repo.Setup(r => r.ObserveCleanupAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string key, AudioLease lease, long now, long next, CancellationToken ct) =>
                { Assert.Equal(now + 4000000, next); Interlocked.Increment(ref Observed); return Task.FromResult(true); });
            repo.Setup(r => r.ReleaseCleanupAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<long>(), "audio.cleanup_failed", It.IsAny<CancellationToken>()))
                .Returns((string key, AudioLease lease, long now, long next, string error, CancellationToken ct) =>
                { Assert.Equal(now + 17000, next); Interlocked.Increment(ref Failures); Released.TrySetResult(); return Task.FromResult(true); });
            var texts = Enumerable.Range(0, textbook ? candidates : mixed ? candidates - candidates / 2 : 0).Select(_ => TextbookUpload(options.Bucket)).ToArray();
            var textRepo = new Mock<ITextbookAudioUploadRepository>();
            textRepo.Setup(r => r.FindRecoverableAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(texts);
            textRepo.Setup(r => r.ClaimCleanupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string key, string owner, long now, long expiry, CancellationToken ct) =>
                { Claims.Add("textbook"); Assert.Equal(now + 40000, expiry); return Task.FromResult<TextbookAudioStoredUpload?>(texts.Single(u => u.Operation.IdempotencyKey == key)); });
            textRepo.Setup(r => r.ObserveCleanupAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string key, AudioLease lease, long now, long next, CancellationToken ct) =>
                { Assert.Equal(now + 4000000, next); Interlocked.Increment(ref Observed); return Task.FromResult(true); });
            textRepo.Setup(r => r.ReleaseCleanupAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<long>(), "audio.cleanup_failed", It.IsAny<CancellationToken>()))
                .Returns((string key, AudioLease lease, long now, long next, string error, CancellationToken ct) =>
                { Assert.Equal(now + 17000, next); Interlocked.Increment(ref Failures); Released.TrySetResult(); return Task.FromResult(true); });
            var services = new ServiceCollection(); services.AddScoped(_ => { Scopes.Add(Guid.NewGuid()); return repo.Object; }); services.AddScoped(_ => textRepo.Object); _provider = services.BuildServiceProvider();
            Worker = new(_provider.GetRequiredService<IServiceScopeFactory>(), Runtime, Clock, Logger.Object);
        }
        public async Task Change(Action<AudioOptions> change)
        { var options = Runtime.CopyCurrentOptions(); change(options); using var config = Config(options); using var prepared = await Runtime.PrepareAsync(config, Ct); Assert.NotNull(prepared); prepared.Publish(); }
        public void Dispose() { Worker.Dispose(); _provider.Dispose(); Runtime.Dispose(); }
        private static TextbookAudioStoredUpload TextbookUpload(string bucket)
        {
            var id = Guid.NewGuid().ToString(); var lease = new AudioLease("cleanup", 1, 40000);
            return new(new(id, "hash", "book", "unit", "track", Guid.NewGuid().ToString(), 10, id,
                AudioUploadState.Abandoned, 0, 0, new(null, 1, null), new(503, null, null, null, null, "audio.upload_failed", 0),
                new(lease, AudioUploadState.Abandoned, 1, 0, null, null)),
                new(id, new(bucket, $"audio/{id.Replace("-", "")}.mp3"), "hash", 128, "audio/mpeg", 0, null));
        }
        private static AudioStoredUpload Upload(string bucket)
        {
            var id = Guid.NewGuid().ToString(); var lease = new AudioLease("cleanup", 1, 40000);
            return new(new(id, "hash", "word", null, AudioLocale.EnGb, "revision", null, id, null, null, AudioUploadState.Abandoned,
                0, 0, lease, new(503, null, null, "audio.storage_failed", 0), new(lease, AudioUploadState.Abandoned, 1, 0, null, null)),
                new(id, new(bucket, $"audio/{id.Replace("-", "")}.mp3"), "hash", 128, "audio/mpeg", 0, null));
        }
    }
    private sealed class Stores : IAudioObjectStoreFactory
    {
        public readonly List<Store> Created = [];
        public IAudioObjectStore Create(AudioOptions options) { var store = new Store(options); Created.Add(store); return store; }
    }
    private sealed class Store(AudioOptions options) : IAudioObjectStore, IDisposable
    {
        public AudioOptions Options { get; } = options; public string? PauseAt; public readonly List<string> Calls = []; public int Disposed;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Finish = new(TaskCreationOptions.RunContinuationsAsynchronously), Called = new(TaskCreationOptions.RunContinuationsAsynchronously), DisposedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Call(string name) { Assert.Equal(0, Disposed); Calls.Add(name); Called.TrySetResult(); if (name == PauseAt) { PauseAt = null; Entered.TrySetResult(); await Finish.Task; } Assert.Equal(0, Disposed); }
        public async Task RemoveAsync(AudioObjectLocation location, CancellationToken token) { Assert.Equal(Options.Bucket, location.Bucket); await Call("remove"); }
        public async Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) { await Call("stat"); return null; }
        public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token) => throw new NotSupportedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { Interlocked.Increment(ref Disposed); DisposedSignal.TrySetResult(); }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly List<Timer> _timers = []; public readonly Channel<bool> Created = Channel.CreateUnbounded<bool>();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(100000);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state); lock (_timers) _timers.Add(timer); Created.Writer.TryWrite(true); return timer; }
        public void Fire() { Timer[] timers; lock (_timers) timers = _timers.ToArray(); foreach (var timer in timers) timer.Fire(); }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
