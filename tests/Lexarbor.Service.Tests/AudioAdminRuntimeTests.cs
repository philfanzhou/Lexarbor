using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class AudioAdminRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Queries = ["/admin/audio-targets", "/admin/vocabulary/w/audio"];
    private const string DeleteRoute = "/admin/vocabulary/w/audio/en-GB";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task InitiallyDisabled_EnableCloseReopen_MetadataAndUnbindUseCurrentDecision()
    {
        await using var host = new Host(enabled: false); await host.Seed();
        foreach (var enabled in new[] { false, true, false, true })
        {
            if (enabled != false || host.Acquisitions != 0) await host.Change(o => o.Enabled = enabled);
            foreach (var route in Queries) using (var response = await host.Client.GetAsync(route, Ct)) await Metadata(response, enabled);
            if (!enabled)
            {
                using var disabled = await host.Delete(); Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
                Assert.Equal(host.Asset, await host.Binding());
            }
        }
        using var removed = await host.Delete(); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null(await host.Binding());
        Assert.Equal(11, host.Acquisitions);
        Assert.All(host.Stores.Created, store => Assert.Equal(0, store.Calls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RotationDuringDatabaseWork_RequestKeepsOneGenerationAndHistory(int route)
    {
        await using var host = new Host(holdRecoveryBatch: true); await host.Seed();
        await host.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var history = await host.History(); var old = host.Stores.Created.Single();
        var entered = Signal(); var finish = Signal(); var calls = 0;
        host.Barrier.Before = async token => { if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await finish.Task.WaitAsync(token); } };
        var pending = route == 2 ? host.Delete() : host.Client.GetAsync(Queries[route], Ct);
        await entered.Task.WaitAsync(Ct);
        await host.Change(o => { o.Enabled = false; o.AccessKey = "synthetic-next-access"; o.SecretKey = "synthetic-next-secret"; });
        Assert.Equal(0, old.Disposed);
        foreach (var query in Queries) using (var next = await host.Client.GetAsync(query, Ct)) await Metadata(next, false);
        using (var disabled = await host.Delete()) Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Equal(host.Asset, await host.Binding());
        // Active metadata/unbind references also participate in the destination guard.
        await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => host.Change(o => o.Bucket = "new-bucket"));
        finish.SetResult();
        using (var response = await pending)
        {
            if (route == 2) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            else await Metadata(response, true);
        }
        Assert.Equal(4, host.Acquisitions); Assert.Equal(0, old.Disposed);
        host.RecoveryFinish.SetResult();
        await old.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1, old.Disposed);
        Assert.Equal(route == 2 ? null : host.Asset, await host.Binding());
        Assert.Equal(history, await host.History());
        await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => host.Change(o => o.KeyPrefix = "next/"));
        Assert.All(host.Stores.Created, store => Assert.Equal(0, store.Calls));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CancelOrDomainFailure_ReleasesReferenceAndPreservesBinding(int route, bool failure)
    {
        await using var host = new Host(holdRecoveryBatch: true); await host.Seed();
        await host.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var old = host.Stores.Created.Single(); var entered = Signal(); var finish = Signal();
        host.Barrier.Before = async token =>
        {
            entered.SetResult(); await finish.Task.WaitAsync(token);
            if (failure) throw new Lexarbor.Domain.Exceptions.DomainValidationException("Audio request is invalid.");
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = route == 2 ? host.Delete(cancellation.Token) : host.Client.GetAsync(Queries[route], cancellation.Token);
        await entered.Task.WaitAsync(Ct); await host.Change(o => o.Enabled = false);
        Assert.Equal(0, old.Disposed);
        if (failure) { finish.SetResult(); using var failed = await pending; Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode); }
        else { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        Assert.Equal(0, old.Disposed);
        host.RecoveryFinish.SetResult();
        await old.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1, old.Disposed);
        host.Barrier.Before = _ => Task.CompletedTask;
        using var next = await host.Client.GetAsync(Queries[0], Ct); await Metadata(next, false);
        Assert.Equal(host.Asset, await host.Binding());
        await host.Change(o => o.Enabled = true);
        using var removed = await host.Delete(); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DestinationReservation_WaitersCancelWithoutLeakingOrUsingStartupValue(int route)
    {
        await using var host = new Host(enabled: false, fakeRecords: true, holdRecoveryBatch: true);
        var entered = Signal(); var finish = Signal();
        host.Records.Check = async token => { entered.SetResult(); await finish.Task.WaitAsync(token); return false; };
        var options = host.Runtime.CopyCurrentOptions(); options.Bucket = "next-bucket"; options.Enabled = true;
        var preparing = host.Runtime.PrepareAsync(Config(options), Ct); await entered.Task.WaitAsync(Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = route == 2 ? host.Delete(cancellation.Token) : host.Client.GetAsync(Queries[route], cancellation.Token);
        var waiting = route == 2 ? host.Delete() : host.Client.GetAsync(Queries[route], Ct);
        Assert.False(waiting.IsCompleted); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, host.Acquisitions);
        using var health = await host.Client.GetAsync("/health", Ct); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await host.Seed(); finish.SetResult(); using var prepared = await preparing; Assert.NotNull(prepared); prepared.Publish();
        await host.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var enabledStore = host.Stores.Created.Last();
        using var response = await waiting;
        if (route == 2) Assert.Equal(HttpStatusCode.OK, response.StatusCode); else await Metadata(response, true);
        Assert.Equal(1, host.Acquisitions); Assert.Equal(1, host.Stores.Created.First().Disposed);
        await host.Change(o => o.Enabled = false);
        // The real hosted worker still holds this generation until its repository task completes.
        Assert.Equal(0, enabledStore.Disposed);
        host.RecoveryFinish.SetResult();
        await enabledStore.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        foreach (var store in host.Stores.Created.Take(host.Stores.Created.Count - 1)) Assert.Equal(1, store.Disposed);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("construct")]
    [InlineData("write")]
    [InlineData("replace")]
    public async Task FailedCandidateOrCommit_DoesNotChangeEnabledBindingOrHistory(string failure)
    {
        await using var host = new Host(); await host.Seed(); var beforeHistory = await host.History();
        var old = host.Stores.Created.Single();
        var root = Path.Combine(AppContext.BaseDirectory, "lexarbor-admin-283-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "data", "appsettings.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject { ["Audio"] = JsonSerializer.SerializeToNode(old.Options) }.ToJsonString());
            var before = File.ReadAllBytes(path);
            var coordinator = new PersistentConfigurationCoordinator(new ConfigurationBuilder().AddJsonFile(path).Build(), new FailingFiles(root, failure));
            host.Stores.Fail = failure == "construct";
            var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, new Dictionary<string, JsonNode?>
            { ["Audio:Enabled"] = failure == "invalid", ["Audio:PublicEndpoint"] = failure == "invalid" ? "invalid" : "https://new.example.test/" }, host.Runtime.PrepareAsync, Ct);
            Assert.Equal(failure is "write" or "replace" ? ConfigurationSaveStatus.StorageFailure : ConfigurationSaveStatus.InvalidCandidate, result.Status);
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(0, old.Disposed);
            foreach (var route in Queries) using (var response = await host.Client.GetAsync(route, Ct)) await Metadata(response, true);
            Assert.Equal(host.Asset, await host.Binding()); Assert.Equal(beforeHistory, await host.History());
            using var removed = await host.Delete(); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Equal(beforeHistory, await host.History());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task Metadata(HttpResponseMessage response, bool enabled)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        var text = await response.Content.ReadAsStringAsync(Ct);
        foreach (var forbidden in new[] { "synthetic-", "bucket", "objectKey", "endpoint", "secret", "generation" }) Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(text); var data = json.RootElement.GetProperty("data");
        Assert.Equal(enabled, data.GetProperty("enabled").GetBoolean()); Assert.Equal(2, data.GetProperty("items").GetArrayLength());
    }
    private static IConfigurationRoot Config(AudioOptions options) => new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }))).Build();
    private sealed class FailingFiles(string root, string failure) : PersistentConfigurationFileStore(root)
    {
        protected override async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken token) { await base.WriteTemporaryAsync(path, bytes, token); if (failure == "write") throw new IOException("synthetic-file-secret"); }
        protected override void Replace(string path, bool overwrite) { if (failure == "replace") throw new IOException("synthetic-file-secret"); base.Replace(path, overwrite); }
    }
    private sealed class Records : IAudioDestinationRecords
    {
        public Func<CancellationToken, Task<bool>> Check = _ => Task.FromResult(false);
        public Task<bool> HasAnyAsync(CancellationToken token) => Check(token);
    }
    internal sealed class Barrier { public Func<CancellationToken, Task> Before = _ => Task.CompletedTask; }
    public class RepositoryProxy<T> : DispatchProxy where T : class
    {
        internal T Inner = null!;
        private Barrier _barrier = null!;
        private Barrier _recoveryBarrier = null!;
        internal static T Wrap(T inner, Barrier barrier, Barrier recoveryBarrier)
        {
            var proxy = Create<T, RepositoryProxy<T>>(); var state = (RepositoryProxy<T>)(object)proxy;
            state.Inner = inner; state._barrier = barrier; state._recoveryBarrier = recoveryBarrier; return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!; var parameters = args!;
            return method.Name switch
            {
                nameof(IAudioReadRepository.SearchTargetsAsync) => Pause<AudioTargetPage>(method, parameters),
                nameof(IAudioReadRepository.GetSnapshotAsync) => Pause<AudioReadSnapshot>(method, parameters),
                nameof(IAudioUploadRepository.UnbindAsync) => Pause<AudioUnbindDisposition>(method, parameters),
                nameof(IAudioUploadRepository.FindRecoverableAsync) => Pause<IReadOnlyList<AudioStoredUpload>>(method, parameters, _recoveryBarrier),
                _ => method.Invoke(Inner, parameters)
            };
        }
        private async Task<TResult> Pause<TResult>(MethodInfo method, object?[] args, Barrier? barrier = null)
        { await (barrier ?? _barrier).Before((CancellationToken)args[^1]!); return await (Task<TResult>)method.Invoke(Inner, args)!; }
    }
    private sealed class Stores : IAudioObjectStoreFactory
    {
        public readonly List<Store> Created = []; public bool Fail;
        public IAudioObjectStore Create(AudioOptions options) { if (Fail) throw new InvalidOperationException("synthetic-client-secret"); var store = new Store(options); Created.Add(store); return store; }
    }
    private sealed class Store(AudioOptions options) : IAudioObjectStore, IDisposable
    {
        public AudioOptions Options { get; } = options; public int Disposed; public int Calls;
        public readonly TaskCompletionSource DisposedSignal = Signal();
        private Task<T> Unexpected<T>() { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Metadata/unbind must not call S3."); }
        public Task PutAsync(AudioObjectLocation location, Stream input, long length, string type, CancellationToken token)
            => PutAsync(location, input, length, type, AudioUploadPurpose.Vocabulary, token);
        public Task PutAsync(AudioObjectLocation location, Stream input, long length, string type, AudioUploadPurpose purpose, CancellationToken token) => Unexpected<bool>();
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => Unexpected<bool>();
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => Unexpected<AudioObjectInfo?>();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => Unexpected<AudioPlaybackLink>();
        public void Dispose() { Interlocked.Increment(ref Disposed); DisposedSignal.TrySetResult(); }
    }
    private sealed class CountedOperations(AudioRuntimeConfiguration runtime, Action acquired) : IAudioAdminOperationFactory
    {
        public async Task<AudioAdminRequestOperation> AcquireAsync(CancellationToken token) { var operation = await new AudioAdminOperationFactory(runtime).AcquireAsync(token); acquired(); return operation; }
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly Stores Stores = new(); public readonly Records Records = new(); public readonly Barrier Barrier = new();
        public readonly TaskCompletionSource RecoveryEntered = Signal(); public readonly TaskCompletionSource RecoveryFinish = Signal();
        private readonly Barrier _recoveryBarrier = new();
        public readonly VocabularyWebApplicationFactory Factory; public readonly WebApplicationFactory<Program> App; public readonly HttpClient Client;
        public readonly string Asset = Guid.NewGuid().ToString(); public int Acquisitions;
        public AudioRuntimeConfiguration Runtime => App.Services.GetRequiredService<AudioRuntimeConfiguration>();
        public Host(bool enabled = true, bool fakeRecords = false, bool holdRecoveryBatch = false)
        {
            if (holdRecoveryBatch) _recoveryBarrier.Before = async token => { RecoveryEntered.TrySetResult(); await RecoveryFinish.Task.WaitAsync(token); };
            Factory = new("Testing", true, extraConfiguration: new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = enabled.ToString(),
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:PublicEndpoint"] = "https://old.example.test/",
                ["Audio:AccessKey"] = "synthetic-admin-access",
                ["Audio:SecretKey"] = "synthetic-admin-secret"
            });
            App = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(Stores);
                if (fakeRecords) { services.RemoveAll<IAudioDestinationRecords>(); services.AddSingleton<IAudioDestinationRecords>(Records); }
                services.RemoveAll<IAudioReadRepository>(); services.AddScoped<IAudioReadRepository>(sp => RepositoryProxy<IAudioReadRepository>.Wrap(new AudioReadRepository(sp.GetRequiredService<VocabularyDbContext>()), Barrier, _recoveryBarrier));
                services.RemoveAll<IAudioUploadRepository>(); services.AddScoped<IAudioUploadRepository>(sp => RepositoryProxy<IAudioUploadRepository>.Wrap(new AudioUploadRepository(sp.GetRequiredService<VocabularyDbContext>(), sp.GetRequiredService<TimeProvider>()), Barrier, _recoveryBarrier));
                services.RemoveAll<IAudioAdminOperationFactory>(); services.AddScoped<IAudioAdminOperationFactory>(sp => new CountedOperations(sp.GetRequiredService<AudioRuntimeConfiguration>(), () => Interlocked.Increment(ref Acquisitions)));
            }));
            Client = App.CreateClient(new() { BaseAddress = new("https://localhost") }); Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        public async Task Change(Action<AudioOptions> change) { var options = Runtime.CopyCurrentOptions(); change(options); using var prepared = await Runtime.PrepareAsync(Config(options), Ct); Assert.NotNull(prepared); prepared.Publish(); }
        public async Task Seed()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.Add(new() { Id = "w", Word = "tone" });
            db.AudioAssets.Add(new() { Id = Asset, Bucket = Runtime.CopyCurrentOptions().Bucket, ObjectKey = "audio/" + Asset.Replace("-", "") + ".mp3", Sha256 = new string('a', 64), ByteLength = 128, CreatedAtUnixMs = 1, FirstPublishedAtUnixMs = 2 });
            db.VocabularyAudioBindings.Add(new() { Id = Asset, WordId = "w", Locale = AudioLocale.EnGb, AssetId = Asset, SourceRevision = new string('a', 64) });
            db.AudioUploadOperations.Add(new() { IdempotencyKey = Guid.NewGuid().ToString(), RequestFingerprint = new string('b', 64), WordId = "w", Locale = AudioLocale.EnGb, SourceRevision = new string('a', 64), AssetId = Asset, State = AudioUploadState.Succeeded, ReceiptStatusCode = 200, ReceiptAssetId = Asset, ReceiptSourceRevision = new string('a', 64), CreatedAtUnixMs = 1, UpdatedAtUnixMs = 2, CompletedAtUnixMs = 2 });
            await db.SaveChangesAsync(Ct);
        }
        public async Task<HttpResponseMessage> Delete(CancellationToken? token = null)
        { using var request = new HttpRequestMessage(HttpMethod.Delete, DeleteRoute); request.Headers.Add("If-Match", "\"" + Asset + "\""); return await Client.SendAsync(request, token ?? Ct); }
        public async Task<string?> Binding() { using var scope = App.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().VocabularyAudioBindings.Select(b => b.AssetId).SingleOrDefaultAsync(Ct); }
        public async Task<string> History()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            return JsonSerializer.Serialize(new { assets = await db.AudioAssets.AsNoTracking().ToListAsync(Ct), operations = await db.AudioUploadOperations.AsNoTracking().ToListAsync(Ct) });
        }
        public async ValueTask DisposeAsync() { RecoveryFinish.TrySetResult(); Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
}
