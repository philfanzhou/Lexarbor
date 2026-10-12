using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

public sealed class AudioRuntimeConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "audio-runtime-tests", $"lexarbor-audio-runtime-{Guid.NewGuid():N}");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private string FilePath => Path.Combine(_root, "data", "appsettings.json");

    [Fact]
    public async Task DisabledEnableDisableReenable_PreservesCompleteFieldsAndOfflineLimits()
    {
        var options = AudioStorageTests.ValidOptions();
        options.Enabled = false;
        options.MaxConcurrentUploads = 3;
        options.RecoveryBatchSize = 7;
        var factory = new StoreFactory();
        using var runtime = Create(options, factory);
        var coordinator = Coordinator(options);
        using var initial = runtime.Acquire();
        Assert.False(initial.Snapshot.Enabled);
        Assert.Equal(ConfigurationSaveStatus.Success, (await Save(coordinator, runtime, new() { ["Audio:Enabled"] = true })).Status);
        using var enabled = runtime.Acquire();
        Assert.True(enabled.Snapshot.Enabled);
        Assert.Equal(3, enabled.Snapshot.MaxConcurrentUploads);
        Assert.Equal(7, enabled.Snapshot.RecoveryBatchSize);
        Assert.Equal(ConfigurationSaveStatus.Success, (await Save(coordinator, runtime, new() { ["Audio:Enabled"] = false })).Status);
        using var disabled = runtime.Acquire();
        Assert.False(disabled.Snapshot.Enabled);
        Assert.Equal(enabled.Snapshot.Bucket, disabled.Snapshot.Bucket);
        Assert.Equal(enabled.Snapshot.PublicEndpoint, disabled.Snapshot.PublicEndpoint);
        Assert.Equal(ConfigurationSaveStatus.Success, (await Save(coordinator, runtime, new() { ["Audio:Enabled"] = true })).Status);
        using var reenabled = runtime.Acquire();
        Assert.True(reenabled.Snapshot.Enabled);
        Assert.Equal(options.AccessKey, factory.Stores.Last().Options.AccessKey);
        Assert.Equal(options.SecretKey, factory.Stores.Last().Options.SecretKey);
        Assert.True(reenabled.Snapshot.Generation > enabled.Snapshot.Generation);
        var changedLimits = OptionsJson(options);
        changedLimits["Audio"]!["MaxConcurrentUploads"] = 8;
        Assert.Null(await runtime.PrepareAsync(Config(changedLimits), _ct));
        Assert.Equal(3, reenabled.Snapshot.MaxConcurrentUploads);
    }

    [Fact]
    public async Task RotationDuringTwoAwaitedOperations_WaitsForActualLastReleaseAndDisposesOnce()
    {
        var options = AudioStorageTests.ValidOptions();
        var factory = new StoreFactory();
        using var runtime = Create(options, factory);
        var coordinator = Coordinator(options);
        var old = factory.Stores.Single();
        var first = runtime.Acquire();
        var second = runtime.Acquire();
        var operation1 = HoldOperation(first, old);
        var operation2 = HoldOperation(second, old);
        await old.Started.Task.WaitAsync(_ct);
        Assert.Equal(ConfigurationSaveStatus.Success, (await Save(coordinator, runtime, new()
        {
            ["Audio:AccessKey"] = "synthetic-rotated-access",
            ["Audio:SecretKey"] = "synthetic-rotated-secret",
            ["Audio:PublicEndpoint"] = "https://new.example.test/media/"
        })).Status);
        using var current = runtime.Acquire();
        Assert.NotEqual(first.Snapshot.Generation, current.Snapshot.Generation);
        Assert.Equal(options.PublicEndpoint, first.Snapshot.PublicEndpoint);
        Assert.Equal("https://new.example.test/media/", current.Snapshot.PublicEndpoint);
        Assert.Equal(options.SecretKey, old.Options.SecretKey);
        Assert.Equal("synthetic-rotated-secret", factory.Stores.Last().Options.SecretKey);
        Assert.Equal(0, old.DisposeCount);
        old.Finish.TrySetResult();
        await operation1;
        Assert.Equal(0, old.DisposeCount);
        old.FinishSecond.TrySetResult();
        await operation2;
        first.Dispose();
        second.Dispose();
        Assert.Equal(1, old.DisposeCount);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("binder")]
    [InlineData("construct")]
    [InlineData("write")]
    [InlineData("replace")]
    [InlineData("cancel-before")]
    [InlineData("cancel-after")]
    public async Task PreparationStorageAndCancellation_KeepOldGenerationUntilCommit(string phase)
    {
        var options = AudioStorageTests.ValidOptions();
        var factory = new StoreFactory();
        using var runtime = Create(options, factory);
        using var cancellation = new CancellationTokenSource();
        var coordinator = Coordinator(options, new FaultStore(_root, phase, cancellation));
        var originalFile = File.ReadAllBytes(FilePath);
        using var original = runtime.Acquire();
        var revision = (await coordinator.ReadAsync(_ct)).Revision;
        factory.ThrowOnCreate = phase == "construct";
        var fields = new Dictionary<string, JsonNode?> { ["Audio:SecretKey"] = JsonValue.Create("synthetic-secret-marker") };
        if (phase == "invalid") fields["Audio:PublicEndpoint"] = JsonValue.Create("invalid-secret-marker");
        var result = default(ConfigurationSaveResult);
        Func<Task> save = async () => result = await coordinator.SaveAsync(revision, fields, (candidate, token) =>
        {
            if (phase == "binder") candidate = Config(new JsonObject { ["Audio"] = new JsonObject { ["MaxUploadBytes"] = "synthetic-secret-marker" } });
            return runtime.PrepareAsync(candidate, token);
        }, cancellation.Token);
        if (phase == "cancel-before") await Assert.ThrowsAnyAsync<OperationCanceledException>(save);
        else await save();
        using var current = runtime.Acquire();
        var committed = phase == "cancel-after";
        Assert.Equal(committed, current.Snapshot.Generation != original.Snapshot.Generation);
        if (!committed) Assert.Equal(originalFile, File.ReadAllBytes(FilePath));
        else Assert.True(cancellation.IsCancellationRequested);
        if (result is not null)
        {
            Assert.Equal(committed ? ConfigurationSaveStatus.Success
                : phase is "write" or "replace" ? ConfigurationSaveStatus.StorageFailure : ConfigurationSaveStatus.InvalidCandidate, result.Status);
            Assert.DoesNotContain("secret-marker", JsonSerializer.Serialize(result));
        }
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(current.Snapshot));
        Assert.DoesNotContain("AccessKey", JsonSerializer.Serialize(current.Snapshot));
        Assert.DoesNotContain("SecretKey", current.Snapshot.ToString());
        Assert.Equal(0, factory.Stores[0].DisposeCount);
        foreach (var candidate in factory.Stores.Skip(1)) Assert.Equal(committed ? 0 : 1, candidate.DisposeCount);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task ShutdownWithOutstandingOperationAndPreparedCandidate_DelaysOldDisposalAndCannotBreakCommittedPublication()
    {
        var options = AudioStorageTests.ValidOptions();
        var factory = new StoreFactory();
        var runtime = Create(options, factory);
        var old = factory.Stores.Single();
        var reference = runtime.Acquire();
        var coordinator = Coordinator(options);
        var result = await coordinator.SaveAsync((await coordinator.ReadAsync(_ct)).Revision,
            new Dictionary<string, JsonNode?> { ["Audio:Enabled"] = false }, async (candidate, token) =>
            {
                var prepared = await runtime.PrepareAsync(candidate, token);
                runtime.Dispose();
                runtime.Dispose();
                return prepared;
            }, _ct);
        Assert.Equal(ConfigurationSaveStatus.Success, result.Status);
        Assert.Throws<ObjectDisposedException>(() => runtime.Acquire());
        Assert.Equal(0, old.DisposeCount);
        Assert.Equal(1, factory.Stores[1].DisposeCount);
        reference.Dispose();
        reference.Dispose();
        Assert.Equal(1, old.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentAcquisitionAndSerializedRotation_AlwaysObserveOneCompleteGeneration()
    {
        var options = AudioStorageTests.ValidOptions();
        var factory = new StoreFactory();
        using var runtime = Create(options, factory);
        var coordinator = Coordinator(options);
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                using var reference = runtime.Acquire();
                var store = Assert.IsType<Store>(reference.ObjectStore);
                Assert.Equal(store.Options.PublicEndpoint, reference.Snapshot.PublicEndpoint);
                Assert.Equal(store.Options.Bucket, reference.Snapshot.Bucket);
                Assert.Equal(0, store.DisposeCount);
            }
        }, _ct)).ToArray();
        for (var i = 0; i < 20; i++)
            Assert.Equal(ConfigurationSaveStatus.Success, (await Save(coordinator, runtime,
                new() { ["Audio:PublicEndpoint"] = $"https://generation-{i}.example.test/" })).Status);
        await Task.WhenAll(readers);
        runtime.Dispose();
        Assert.All(factory.Stores, store => Assert.Equal(1, store.DisposeCount));
    }

    [Fact]
    public async Task ConcurrentSavesAtOneRevision_CommitExactlyOneWholeGeneration()
    {
        var options = AudioStorageTests.ValidOptions();
        var factory = new StoreFactory();
        using var runtime = Create(options, factory);
        var coordinator = Coordinator(options);
        var revision = (await coordinator.ReadAsync(_ct)).Revision;
        var saves = Enumerable.Range(0, 2).Select(index => coordinator.SaveAsync(revision,
            new Dictionary<string, JsonNode?>
            {
                ["Audio:PublicEndpoint"] = JsonValue.Create($"https://winner-{index}.example.test/"),
                ["Audio:SecretKey"] = JsonValue.Create($"synthetic-winner-{index}")
            }, runtime.PrepareAsync, _ct)).ToArray();
        var results = await Task.WhenAll(saves);
        var winner = Array.FindIndex(results, result => result.Status == ConfigurationSaveStatus.Success);
        Assert.Single(results, result => result.Status == ConfigurationSaveStatus.Success);
        Assert.Single(results, result => result.Status == ConfigurationSaveStatus.Conflict);
        using var current = runtime.Acquire();
        Assert.Equal($"https://winner-{winner}.example.test/", current.Snapshot.PublicEndpoint);
        Assert.Equal($"synthetic-winner-{winner}", Assert.IsType<Store>(current.ObjectStore).Options.SecretKey);
        Assert.Equal(2, factory.Stores.Count);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task FailedAwaitedOperation_ReleasesRetiredGenerationInFinally(string phase)
    {
        var factory = new StoreFactory();
        using var runtime = Create(AudioStorageTests.ValidOptions(), factory);
        var reference = runtime.Acquire();
        var old = factory.Stores.Single();
        var operation = Task.Run(async () =>
        {
            using (reference)
            {
                await old.Started.Task.WaitAsync(_ct);
                if (phase == "cancel") throw new OperationCanceledException();
                throw new AudioStorageException("audio.storage.timeout", "Audio storage operation timed out.");
            }
        }, _ct);
        using var prepared = await runtime.PrepareAsync(Config(OptionsJson(AudioStorageTests.ValidOptions())), _ct);
        prepared!.Publish();
        Assert.Equal(0, old.DisposeCount);
        old.Started.TrySetResult();
        if (phase == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else await Assert.ThrowsAsync<AudioStorageException>(() => operation);
        Assert.Equal(1, old.DisposeCount);
    }

    [Fact]
    public async Task ConstructionAndDisposalOccurOutsideShortLock_AndOptionsAreCopied()
    {
        var options = AudioStorageTests.ValidOptions();
        var factory = new StoreFactory();
        using var runtime = Create(options, factory);
        options.PublicEndpoint = "https://mutated.example.test/";
        factory.OnCreate = () => Task.Run(() => { using var reference = runtime.Acquire(); }).Wait(TimeSpan.FromSeconds(5));
        using var prepared = await runtime.PrepareAsync(Config(OptionsJson(AudioStorageTests.ValidOptions())), _ct);
        Assert.NotNull(prepared);
        factory.Stores[0].OnDispose = () =>
        {
            Assert.True(Task.Run(() => { using var reference = runtime.Acquire(); }).Wait(TimeSpan.FromSeconds(5)));
            throw new InvalidOperationException("synthetic-secret-marker");
        };
        prepared.Publish();
        using var current = runtime.Acquire();
        Assert.Equal("https://audio.example.test/s3/", current.Snapshot.PublicEndpoint);
        Assert.Equal(1, factory.Stores[0].DisposeCount);
    }

    [Fact]
    public async Task CancelAfterConstructionAndDiscardedCandidate_ReleaseOnlyCandidateOnce()
    {
        var factory = new StoreFactory();
        using var runtime = Create(AudioStorageTests.ValidOptions(), factory);
        using var cancellation = new CancellationTokenSource();
        factory.OnCreate = () => { cancellation.Cancel(); return true; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.PrepareAsync(Config(OptionsJson(AudioStorageTests.ValidOptions())), cancellation.Token));
        Assert.Equal(1, factory.Stores[1].DisposeCount);
        factory.OnCreate = null;
        var prepared = await runtime.PrepareAsync(Config(OptionsJson(AudioStorageTests.ValidOptions())), _ct);
        prepared!.Dispose();
        prepared.Dispose();
        Assert.Equal(1, factory.Stores[2].DisposeCount);
        using var current = runtime.Acquire();
        Assert.Same(factory.Stores[0], current.ObjectStore);
        Assert.Equal(0, factory.Stores[0].DisposeCount);
    }

    [Fact]
    public async Task RealAdapterCopiesStartupOptions_AndSigningRemainsLocal()
    {
        var options = AudioStorageTests.ValidOptions();
        using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
        options.PublicEndpoint = "https://changed.example.test/";
        options.SecretKey = "synthetic-replaced-secret";
        options.Bucket = "different-bucket";
        var link = await store.GetPlaybackLinkAsync(AudioStorageTests.Location(), TimeSpan.FromSeconds(900), _ct);
        Assert.StartsWith("https://audio.example.test/s3/", link.Url);
        Assert.DoesNotContain("synthetic-replaced-secret", link.Url);
        store.Dispose();
    }

    [Fact]
    public void HostRegistersInternalManagerWithoutOpeningS3SaveRoute()
    {
        var configuration = Config(new JsonObject());
        using var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddAudioStorage(configuration).BuildServiceProvider();
        var runtime = services.GetRequiredService<AudioRuntimeConfiguration>();
        using var reference = runtime.Acquire();
        Assert.False(reference.Snapshot.Enabled);
    }

    private async Task HoldOperation(AudioOperationReference reference, Store store)
    {
        using (reference)
            await store.RemoveAsync(AudioStorageTests.Location(), _ct);
    }

    private static AudioRuntimeConfiguration Create(AudioOptions options, StoreFactory factory) => new(Options.Create(options), factory);
    private PersistentConfigurationCoordinator Coordinator(AudioOptions options, PersistentConfigurationFileStore? store = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, OptionsJson(options).ToJsonString());
        return new(new ConfigurationBuilder().AddJsonFile(FilePath).Build(), store ?? new(_root));
    }
    private async Task<ConfigurationSaveResult> Save(PersistentConfigurationCoordinator coordinator, AudioRuntimeConfiguration runtime, Dictionary<string, object> fields)
        => await coordinator.SaveAsync((await coordinator.ReadAsync(_ct)).Revision,
            fields.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToNode(pair.Value)), runtime.PrepareAsync, _ct);
    private static JsonObject OptionsJson(AudioOptions options) => new() { ["Audio"] = JsonSerializer.SerializeToNode(options) };
    private static IConfigurationRoot Config(JsonObject json) => new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(json))).Build();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FaultStore(string root, string phase, CancellationTokenSource cancellation) : PersistentConfigurationFileStore(root)
    {
        protected override async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            await base.WriteTemporaryAsync(path, bytes, cancellationToken);
            if (phase == "write") throw new IOException("synthetic-secret-marker");
            if (phase == "cancel-before") cancellation.Cancel();
        }
        protected override void Replace(string temporaryPath, bool overwrite)
        {
            if (phase == "replace") throw new IOException("synthetic-secret-marker");
            base.Replace(temporaryPath, overwrite);
            if (phase == "cancel-after") cancellation.Cancel();
        }
    }

    private sealed class StoreFactory : IAudioObjectStoreFactory
    {
        public List<Store> Stores { get; } = [];
        public bool ThrowOnCreate { get; set; }
        public Func<bool>? OnCreate { get; set; }
        public IAudioObjectStore Create(AudioOptions options)
        {
            if (ThrowOnCreate) throw new InvalidOperationException("synthetic-secret-marker");
            if (OnCreate is not null) Assert.True(OnCreate());
            var store = new Store(options);
            Stores.Add(store);
            return store;
        }
    }

    private sealed class Store(AudioOptions options) : IAudioObjectStore, IDisposable
    {
        public AudioOptions Options { get; } = options;
        public int DisposeCount;
        public Action? OnDispose { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _operations;
        public async Task RemoveAsync(AudioObjectLocation location, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _operations);
            if (number == 2) Started.TrySetResult();
            await (number == 1 ? Finish.Task : FinishSecond.Task).WaitAsync(cancellationToken);
            Assert.Equal(0, DisposeCount);
        }
        public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { Interlocked.Increment(ref DisposeCount); OnDispose?.Invoke(); }
    }
}
