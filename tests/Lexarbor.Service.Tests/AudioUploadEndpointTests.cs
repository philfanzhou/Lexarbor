using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class AudioUploadEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] Mp3 => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    private sealed class Store : IAudioObjectStore, IDisposable
    {
        public int Calls;
        public int Disposals;
        public AudioOptions Options = new();
        public readonly List<AudioObjectLocation> Locations = [];
        public void Dispose() => Interlocked.Increment(ref Disposals);
        public Func<CancellationToken, Task>? BeforePut;
        public readonly Dictionary<string, byte[]> Objects = [];
        public Task PutAsync(AudioObjectLocation location, Stream stream, long length, string type, CancellationToken token)
            => PutAsync(location, stream, length, type, AudioUploadPurpose.Vocabulary, token);
        public async Task PutAsync(AudioObjectLocation location, Stream stream, long length, string type, AudioUploadPurpose purpose, CancellationToken token)
        {
            lock (Locations) Locations.Add(location);
            Assert.Equal(0, Disposals);
            Interlocked.Increment(ref Calls);
            if (BeforePut != null) await BeforePut(token);
            using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes, token);
            lock (Objects) Objects.Add(location.Key, bytes.ToArray());
        }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => throw new NotImplementedException();
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new NotImplementedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan ttl, CancellationToken token) => throw new NotImplementedException();
    }
    private sealed class TestStoreFactory(Store store) : IAudioObjectStoreFactory
    {
        public List<Store> Stores { get; } = [];
        public IAudioObjectStore Create(AudioOptions options)
        {
            var result = Stores.Count == 0 ? store : new Store();
            result.Options = options;
            Stores.Add(result);
            return result;
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly Store Store = new();
        public readonly VocabularyWebApplicationFactory Factory;
        public readonly TestStoreFactory Stores;
        public AudioRuntimeConfiguration Runtime => App.Services.GetRequiredService<AudioRuntimeConfiguration>();
        public readonly WebApplicationFactory<Program> App;
        public readonly HttpClient Client;
        public Host(bool enabled = true, long maxBytes = 5 * 1024 * 1024, int concurrency = 2, int queue = 8, int timeout = 60, TimeProvider? clock = null, string? crash = null, Func<CancellationToken, Task>? beforeReserve = null, Func<CancellationToken, Task>? beforeRead = null)
        {
            Stores = new(Store);
            Factory = new("Testing", true, extraConfiguration: new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = enabled.ToString(),
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:PublicEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:AccessKey"] = "synthetic",
                ["Audio:SecretKey"] = "synthetic",
                ["Audio:MaxUploadBytes"] = maxBytes.ToString(),
                ["Audio:MaxConcurrentUploads"] = concurrency.ToString(),
                ["Audio:MaxQueuedUploads"] = queue.ToString(),
                ["Audio:OperationTimeoutSeconds"] = timeout.ToString()
            });
            App = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAudioObjectStore>(); services.AddSingleton<IAudioObjectStore>(Store);
                services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(Stores);
                if (clock != null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
                if (beforeRead != null) services.AddSingleton<IStartupFilter>(new ReadBarrier(beforeRead));
                if (crash != null || beforeReserve != null)
                {
                    services.RemoveAll<IAudioUploadRepository>();
                    services.AddScoped(provider => FaultRepository(provider.GetRequiredService<VocabularyDbContext>(), crash, beforeReserve));
                }
            }));
            Client = App.CreateClient(new() { BaseAddress = new("https://localhost") });
            Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        private static IAudioUploadRepository FaultRepository(VocabularyDbContext db, string? crash, Func<CancellationToken, Task>? beforeReserve)
        {
            var actual = new AudioUploadRepository(db);
            var mock = new Mock<IAudioUploadRepository>();
            mock.Setup(repo => repo.ReserveAsync(It.IsAny<AudioUploadRequest>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(async (AudioUploadRequest request, string owner, long now, long expires, CancellationToken token) =>
                {
                    if (beforeReserve != null) await beforeReserve(token);
                    return await actual.ReserveAsync(request, owner, now, expires, token);
                });
            mock.Setup(repo => repo.BeginTransferAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string key, AudioLease lease, long now, CancellationToken token) => actual.BeginTransferAsync(key, lease, now, token));
            mock.Setup(repo => repo.CompleteAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(async (string key, AudioLease lease, long now, CancellationToken token) =>
                {
                    if (crash == null) return await actual.CompleteAsync(key, lease, now, token);
                    if (crash == "after-commit") await actual.CompleteAsync(key, lease, now, token);
                    throw new IOException("Injected process/response loss.");
                });
            mock.Setup(repo => repo.AbandonAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, AudioLease lease, long now, int status, string error, CancellationToken token) => actual.AbandonAsync(key, lease, now, status, error, token));
            mock.Setup(repo => repo.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken token) => actual.GetAsync(key, token));
            mock.Setup(repo => repo.ExpireAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string key, long now, CancellationToken token) => actual.ExpireAsync(key, now, token));
            return mock.Object;
        }
        public async Task PublishAsync(Action<AudioOptions> change)
        {
            var options = Runtime.CopyCurrentOptions();
            change(options);
            var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
                new { Audio = options }))).Build();
            using var candidate = await Runtime.PrepareAsync(configuration, Ct);
            Assert.NotNull(candidate);
            candidate.Publish();
        }
        public async Task SeedAsync()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.AddRange(new VocabularyEntity { Id = "w", Word = "Apple", PhoneticUk = "uk", PhoneticUs = "us" },
                new VocabularyEntity { Id = "other", Word = "Other" });
            db.VocabularyBooks.Add(new() { Id = "disabled", BookName = "Disabled", Status = false });
            db.VocabularyMeanings.Add(new() { Id = "m", VocabularyId = "w", BookId = "disabled", PartOfSpeech = "n.", Meaning = "fruit" });
            await db.SaveChangesAsync(Ct);
        }
        public async Task<string> Revision(string locale = "en-GB", string? meaning = null)
        {
            using var scope = App.Services.CreateScope();
            var snapshot = await scope.ServiceProvider.GetRequiredService<IAudioReadRepository>().GetSnapshotAsync("w", meaning, Ct);
            return AudioSelectionService.Select(snapshot, AudioLocales.FromCode(locale)).SourceRevision;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
    private sealed class ReadBarrier(Func<CancellationToken, Task> pause) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, invoke) =>
            {
                if (context.Request.Method == "PUT" && context.Request.Path.Value!.Contains("/audio/", StringComparison.Ordinal))
                    context.Request.Body = new PausedReadStream(context.Request.Body, pause);
                await invoke();
            });
            next(app);
        };
    }
    private sealed class PausedReadStream(Stream inner, Func<CancellationToken, Task> pause) : Stream
    {
        private bool _paused;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_paused) { _paused = true; await pause(token); }
            return await inner.ReadAsync(buffer, token);
        }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static MultipartFormDataContent Body(string revision, string? expected = "", byte[]? bytes = null, bool includeExpected = true)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(revision), "sourceRevision");
        if (includeExpected) body.Add(new StringContent(expected ?? ""), "expectedAssetId");
        body.Add(new ByteArrayContent(bytes ?? Mp3), "file", "untrusted-name.mp3"); return body;
    }
    private static async Task<HttpResponseMessage> Put(Host host, string revision, string? expected = "", string? key = null,
        string path = "/admin/vocabulary/w/audio/en-GB", byte[]? bytes = null, bool includeExpected = true, CancellationToken? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = Body(revision, expected, bytes, includeExpected) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await host.Client.SendAsync(request, token ?? Ct);
    }
    private static async Task<string> Asset(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("data").GetProperty("assetId").GetString()!;
    }

    [Fact]
    public async Task DefaultMeaningAndLocales_AreIndependent_AndHistoryIsRetained()
    {
        await using var host = new Host(); await host.SeedAsync();
        var gb = await host.Revision(); var us = await host.Revision("en-US"); var meaning = await host.Revision(meaning: "m");
        using var first = await Put(host, gb); var firstId = await Asset(first);
        using var american = await Put(host, us, path: "/admin/vocabulary/w/audio/en-US"); await Asset(american);
        using var special = await Put(host, meaning, path: "/admin/vocabulary/w/audio/en-GB?meaningId=m"); await Asset(special);
        using var replace = await Put(host, gb, firstId); Assert.NotEqual(firstId, await Asset(replace));
        var orphanRevision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("other", "Other", null, null), null, AudioLocale.EnGb));
        using var orphan = await Put(host, orphanRevision, path: "/admin/vocabulary/other/audio/en-GB"); await Asset(orphan);
        Assert.Equal(5, host.Store.Calls);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(4, await db.VocabularyAudioBindings.CountAsync(Ct)); Assert.Equal(5, await db.AudioAssets.CountAsync(Ct));
        Assert.All(await db.AudioAssets.ToListAsync(Ct), asset => Assert.NotNull(asset.FirstPublishedAtUnixMs));
        Assert.All(host.Store.Objects.Values, bytes => Assert.Equal(Mp3, bytes));
        Assert.True(first.Headers.Contains("X-Content-Type-Options"));
        var response = await first.Content.ReadAsStringAsync(Ct);
        foreach (var secret in new[] { "bucket", "key", "url", "synthetic" }) Assert.DoesNotContain(secret, response, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fake", 415)]
    [InlineData("truncated", 415)]
    [InlineData("revision", 400)]
    [InlineData("expected", 400)]
    [InlineData("missing-condition", 400)]
    [InlineData("uuid", 400)]
    [InlineData("locale", 400)]
    [InlineData("meaning", 404)]
    [InlineData("owner", 404)]
    [InlineData("multi-file", 400)]
    [InlineData("duplicate", 400)]
    [InlineData("producer", 400)]
    [InlineData("unknown-field", 400)]
    [InlineData("malformed", 400)]
    [InlineData("oversize", 413)]
    public async Task InvalidRequests_NeverReserveOrPut(string kind, int status)
    {
        await using var host = new Host(maxBytes: kind == "oversize" ? 100 : 5 * 1024 * 1024); await host.SeedAsync();
        var revision = await host.Revision();
        using var body = Body(kind == "revision" ? "bad" : revision, kind == "expected" ? "bad" : "",
            kind == "fake" ? "not mp3"u8.ToArray() : kind == "truncated" ? Mp3[..^1] : null, kind != "missing-condition");
        if (kind == "multi-file") body.Add(new ByteArrayContent(Mp3), "file", "second.mp3");
        if (kind == "duplicate") body.Add(new StringContent(revision), "sourceRevision");
        if (kind == "producer") body.Add(new StringContent(new string('p', 129)), "producer");
        if (kind == "unknown-field") body.Add(new StringContent("anything"), "bucket");
        var path = kind switch { "locale" => "/admin/vocabulary/w/audio/fr-FR", "meaning" => "/admin/vocabulary/w/audio/en-GB?meaningId=missing", "owner" => "/admin/vocabulary/other/audio/en-GB?meaningId=m", _ => "/admin/vocabulary/w/audio/en-GB" };
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = kind == "malformed" ? new StringContent("not multipart") : body };
        request.Headers.Add("Idempotency-Key", kind == "uuid" ? "invalid" : Guid.NewGuid().ToString());
        using var response = await host.Client.SendAsync(request, Ct); Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, host.Store.Calls);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Empty(await db.AudioUploadOperations.ToListAsync(Ct)); Assert.Empty(await db.AudioAssets.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("trailing-lf")]
    [InlineData("trailing-crlf")]
    [InlineData("trailing-cr")]
    [InlineData("leading-lf")]
    [InlineData("trailing-space")]
    [InlineData("leading-space")]
    [InlineData("uppercase")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("non-hex")]
    public async Task InvalidSourceRevision_Returns400WithoutReserveOrPut(string kind)
    {
        await using var host = new Host(); await host.SeedAsync();
        var revision = await host.Revision();
        var invalid = kind switch
        {
            "trailing-lf" => revision + "\n",
            "trailing-crlf" => revision + "\r\n",
            "trailing-cr" => revision + "\r",
            "leading-lf" => "\n" + revision,
            "trailing-space" => revision + " ",
            "leading-space" => " " + revision,
            "uppercase" => new string('A', 64),
            "short" => revision[..^1],
            "long" => revision + "a",
            "non-hex" => "g" + revision[1..],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var response = await Put(host, invalid);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Audio upload conditions are required and must be valid.", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, host.Store.Calls);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Empty(await db.AudioUploadOperations.ToListAsync(Ct));
        Assert.Empty(await db.AudioAssets.ToListAsync(Ct));
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Ct));
    }

    [Fact]
    public async Task ValidSourceRevision_PreservesVersionConflictAndSuccess()
    {
        await using var host = new Host(); await host.SeedAsync();
        var revision = await host.Revision();
        using var mismatch = await Put(host, new string(revision[0] == 'a' ? 'b' : 'a', 64));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal(0, host.Store.Calls);
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Empty(await db.AudioUploadOperations.ToListAsync(Ct));
            Assert.Empty(await db.AudioAssets.ToListAsync(Ct));
        }
        using var valid = await Put(host, revision);
        await Asset(valid);
        Assert.Equal(1, host.Store.Calls);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie", 401)]
    [InlineData("mixed", 403)]
    public async Task Authorization_PrecedesBodyAndRetainsBearerPrecedence(string identity, int status)
    {
        await using var host = new Host(); await host.SeedAsync();
        host.Client.DefaultRequestHeaders.Authorization = identity is "student" or "mixed" ? new("Bearer", host.Factory.CreateToken("student")) : null;
        if (identity is "cookie" or "mixed") host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("admin"));
        using var response = await Put(host, await host.Revision(), bytes: "invalid"u8.ToArray()); Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, host.Store.Calls);
    }

    [Fact]
    public async Task CookieWithCsrfAndBearerWithCookie_CanUpload()
    {
        await using var host = new Host(); await host.SeedAsync();
        var cookie = host.Factory.CreateSessionCookie("admin"); host.Client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var bearer = await Put(host, await host.Revision()); var id = await Asset(bearer);
        host.Client.DefaultRequestHeaders.Authorization = null;
        using var csrf = await host.Client.GetAsync("/admin/auth/csrf", Ct); csrf.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync(Ct));
        host.Client.DefaultRequestHeaders.Remove("Cookie");
        host.Client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + csrf.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        host.Client.DefaultRequestHeaders.Add(AdminTestAntiforgery.HeaderName, json.RootElement.GetProperty("token").GetString());
        using var session = await Put(host, await host.Revision(), id); await Asset(session);
    }

    [Fact]
    public async Task SameKey_ReplaysAfterReplaceDeleteAndEdit_AndRejectsChangedFingerprint()
    {
        await using var host = new Host(); await host.SeedAsync(); var revision = await host.Revision(); var key = Guid.NewGuid().ToString();
        using var first = await Put(host, revision, key: key); var firstId = await Asset(first);
        using var changed = await Put(host, revision, key: key, path: "/admin/vocabulary/w/audio/en-US"); Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var replace = await Put(host, revision, firstId); var newer = await Asset(replace);
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Vocabularies.Where(word => word.Id == "w")
                .ExecuteUpdateAsync(set => set.SetProperty(word => word.Word, "Edited"), Ct);
        using var retry = await Put(host, revision, key: key); Assert.Equal(firstId, await Asset(retry)); Assert.Equal(2, host.Store.Calls);
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(newer, (await db.VocabularyAudioBindings.SingleAsync(Ct)).AssetId);
            await db.Vocabularies.Where(word => word.Id == "w").ExecuteDeleteAsync(Ct);
        }
        using var deletedRetry = await Put(host, revision, key: key); Assert.Equal(firstId, await Asset(deletedRetry)); Assert.Equal(2, host.Store.Calls);
    }

    [Fact]
    public async Task ConcurrentReplacements_PublishOnlyOne_AndOtherVocabularyWritesAdvance()
    {
        await using var host = new Host(); await host.SeedAsync(); var revision = await host.Revision();
        using var initial = await Put(host, revision); var old = await Asset(initial);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        host.Store.BeforePut = async token => { if (Interlocked.Increment(ref count) == 2) arrived.SetResult(); await release.Task.WaitAsync(token); };
        var a = Put(host, revision, old); var b = Put(host, revision, old); await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.Vocabularies.Where(word => word.Id == "other").ExecuteUpdateAsync(set => set.SetProperty(word => word.Word, "Updated"), Ct);
        }
        release.SetResult(); using var one = await a; using var two = await b;
        Assert.Equal(new[] { 200, 409 }, new[] { (int)one.StatusCode, (int)two.StatusCode }.Order());
        using var finalScope = host.App.Services.CreateScope(); var finalDb = finalScope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(2, await finalDb.AudioAssets.CountAsync(asset => asset.FirstPublishedAtUnixMs != null, Ct));
        Assert.Equal(1, await finalDb.AudioUploadOperations.CountAsync(op => op.State == AudioUploadState.Abandoned, Ct));
    }

    [Theory]
    [InlineData("edit", 409)]
    [InlineData("import", 409)]
    [InlineData("delete", 404)]
    [InlineData("meaning-delete", 404)]
    public async Task TargetChangedDuringPut_DoesNotPublish(string change, int status)
    {
        await using var host = new Host(); await host.SeedAsync(); var meaning = change == "meaning-delete" ? "m" : null;
        var revision = await host.Revision(meaning: meaning);
        host.Store.BeforePut = async _ =>
        {
            using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            if (change == "import")
            {
                await db.VocabularyBooks.Where(book => book.Id == "disabled").ExecuteUpdateAsync(set => set.SetProperty(book => book.Status, true), Ct);
                await scope.ServiceProvider.GetRequiredService<VocabularyDomainService>().ImportBatchAsync("disabled",
                    [(new() { Word = "Apple", PhoneticUk = "updated" }, new() { PartOfSpeech = "n.", Meaning = "fruit" }, null, null, null)]);
            }
            else if (change == "edit") await db.Vocabularies.Where(word => word.Id == "w").ExecuteUpdateAsync(set => set.SetProperty(word => word.Word, "Edited"), Ct);
            else if (change == "delete") await db.Vocabularies.Where(word => word.Id == "w").ExecuteDeleteAsync(Ct);
            else await db.VocabularyMeanings.Where(row => row.Id == "m").ExecuteDeleteAsync(Ct);
        };
        using var response = await Put(host, revision, path: "/admin/vocabulary/w/audio/en-GB" + (meaning == null ? "" : "?meaningId=m"));
        Assert.Equal(status, (int)response.StatusCode); Assert.Single(host.Store.Objects);
        using var finalScope = host.App.Services.CreateScope(); var finalDb = finalScope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Empty(await finalDb.VocabularyAudioBindings.ToListAsync(Ct));
        Assert.Null((await finalDb.AudioAssets.SingleAsync(Ct)).FirstPublishedAtUnixMs);
        Assert.Equal(status, (await finalDb.AudioUploadOperations.SingleAsync(Ct)).ReceiptStatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutFailureOrTimeout_PersistsSafeFailureAndReplaysWithoutPut(bool timeout)
    {
        await using var host = new Host(timeout: 1); await host.SeedAsync(); var revision = await host.Revision(); var key = Guid.NewGuid().ToString();
        using var initial = await Put(host, revision); var old = await Asset(initial);
        host.Store.BeforePut = token => timeout ? Task.Delay(Timeout.Infinite, token) : Task.FromException(new IOException("synthetic-secret-key-url"));
        using var response = await Put(host, revision, old, key: key); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("synthetic", await response.Content.ReadAsStringAsync(Ct));
        using var retry = await Put(host, revision, old, key: key); Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode); Assert.Equal(2, host.Store.Calls);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(old, (await db.VocabularyAudioBindings.SingleAsync(Ct)).AssetId);
        Assert.Equal(AudioUploadState.Abandoned, (await db.AudioUploadOperations.SingleAsync(op => op.IdempotencyKey == key, Ct)).State);
    }

    [Fact]
    public async Task QueueFull_RejectsOnlyAudio_AndCancellationLeavesRecoverableKey()
    {
        await using var host = new Host(concurrency: 1, queue: 0); await host.SeedAsync(); var revision = await host.Revision();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Store.BeforePut = async token => { arrived.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var cancel = new CancellationTokenSource(); var active = Put(host, revision, token: cancel.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var rejected = await Put(host, revision); Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode); Assert.NotNull(rejected.Headers.RetryAfter);
        using var metadata = await host.Client.GetAsync("/admin/vocabulary/w/audio", Ct); metadata.EnsureSuccessStatusCode();
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        // TestServer request abort propagation and its independent failure transaction complete asynchronously.
        for (var i = 0; i < 100; i++)
        {
            using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            if ((await db.AudioUploadOperations.SingleAsync(Ct)).State == AudioUploadState.Abandoned) return;
            await Task.Delay(10, Ct);
        }
        Assert.Fail("Cancelled upload did not persist its failure receipt.");
    }

    [Fact]
    public async Task DisabledAudio_IsSafe503_AndExistingQueriesRemainAvailable()
    {
        await using var host = new Host(enabled: false); await host.SeedAsync();
        using var response = await Put(host, await host.Revision()); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(0, host.Store.Calls);
        using var metadata = await host.Client.GetAsync("/admin/vocabulary/w/audio", Ct); metadata.EnsureSuccessStatusCode();
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Time = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Time;
    }

    [Fact]
    public async Task ExpiredLease_CannotPublishAfterAProviderReturnsLate()
    {
        var clock = new Clock(); await using var host = new Host(clock: clock); await host.SeedAsync();
        host.Store.BeforePut = _ => { clock.Time = clock.Time.AddMinutes(6); return Task.CompletedTask; };
        using var response = await Put(host, await host.Revision()); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Ct)); Assert.Single(host.Store.Objects);
        Assert.Equal("audio.upload_expired", (await db.AudioUploadOperations.SingleAsync(Ct)).ReceiptErrorCode);
    }

    [Theory]
    [InlineData("after-put", 409, AudioUploadState.Transferring)]
    [InlineData("after-commit", 200, AudioUploadState.Succeeded)]
    public async Task ProcessOrResponseLoss_PreservesPersistentRecoveryOrSuccess(string point, int replayStatus, AudioUploadState state)
    {
        await using var host = new Host(crash: point); await host.SeedAsync(); var revision = await host.Revision(); var key = Guid.NewGuid().ToString();
        using var lost = await Put(host, revision, key: key); Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        using var retry = await Put(host, revision, key: key); Assert.Equal(replayStatus, (int)retry.StatusCode); Assert.Equal(1, host.Store.Calls);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var operation = await db.AudioUploadOperations.SingleAsync(Ct);
        Assert.Equal(state, operation.State); Assert.Single(host.Store.Objects);
        Assert.Equal(point == "after-commit" ? 1 : 0, await db.VocabularyAudioBindings.CountAsync(Ct));
        Assert.Equal(point == "after-commit", (await db.AudioAssets.SingleAsync(Ct)).FirstPublishedAtUnixMs != null);
        if (point == "after-put") Assert.Null(operation.ReceiptStatusCode);
        else Assert.Equal(200, operation.ReceiptStatusCode);
    }

    [Theory]
    [InlineData("header-missing", 400)]
    [InlineData("header-multiple", 400)]
    [InlineData("boundary", 400)]
    [InlineData("chunked-overhead", 413)]
    [InlineData("id3-overflow", 415)]
    public async Task UntrustedHeadersAndChunkedBodies_AreBounded(string kind, int status)
    {
        await using var host = new Host(maxBytes: 1024 * 1024); await host.SeedAsync(); var revision = await host.Revision();
        HttpContent body = Body(revision);
        if (kind == "boundary") { body.Dispose(); body = new StringContent("invalid"); body.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + new string('x', 129)); }
        if (kind == "id3-overflow") { body.Dispose(); body = Body(revision, bytes: new byte[] { 73, 68, 51, 4, 0, 0, 127, 127, 127, 127 }); }
        if (kind == "chunked-overhead")
        {
            var bytes = await body.ReadAsByteArrayAsync(Ct); var type = body.Headers.ContentType!.ToString(); body.Dispose();
            body = new StreamContent(new NonSeekable(new MemoryStream(bytes.Concat(new byte[65537]).ToArray())));
            body.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/vocabulary/w/audio/en-GB") { Content = body };
        if (kind != "header-missing") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (kind == "header-multiple") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await host.Client.SendAsync(request, Ct); Assert.Equal(status, (int)response.StatusCode); Assert.Equal(0, host.Store.Calls);
    }
    private sealed class NonSeekable(Stream stream) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => stream.ReadAsync(buffer, token);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    [Theory]
    [InlineData("parse")]
    [InlineData("reserve")]
    [InlineData("put")]
    public async Task RotationAtEveryUploadBoundary_UsesOneGenerationAndRejectsDestinationChange(string phase)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task Pause(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1) { arrived.TrySetResult(); await release.Task.WaitAsync(token); }
        }
        await using var host = new Host(beforeReserve: phase == "reserve" ? Pause : null, beforeRead: phase == "parse" ? Pause : null);
        await host.SeedAsync();
        if (phase == "put") host.Store.BeforePut = Pause;
        var old = Put(host, await host.Revision());
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var before = host.Runtime.CopyCurrentOptions();
        await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => host.PublishAsync(options => options.Bucket = "next-bucket"));
        Assert.Equal(before.Bucket, host.Runtime.CopyCurrentOptions().Bucket);
        await host.PublishAsync(options =>
        {
            options.AccessKey = "synthetic-next-access";
            options.SecretKey = "synthetic-next-secret";
            options.PublicEndpoint = "https://new.example.test/media/";
        });
        Assert.Equal(0, host.Store.Disposals);
        using var newer = await Put(host, await host.Revision("en-US"), path: "/admin/vocabulary/w/audio/en-US");
        await Asset(newer);
        var newStore = host.Stores.Stores.Last();
        Assert.Equal("synthetic-next-secret", newStore.Options.SecretKey);
        Assert.Equal("synthetic", host.Store.Options.SecretKey);
        Assert.Equal("https://new.example.test/media/", newStore.Options.PublicEndpoint);
        // This write succeeds while storage/parse is paused: no long SQLite transaction.
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Vocabularies.Where(word => word.Id == "other")
                .ExecuteUpdateAsync(set => set.SetProperty(word => word.Word, "During rotation"), Ct);
        release.TrySetResult();
        using var prior = await old;
        await Asset(prior);
        Assert.Equal(1, host.Store.Calls);
        Assert.Equal(1, host.Store.Disposals);
        Assert.All(host.Store.Locations, location => Assert.Equal(host.Store.Options.Bucket, location.Bucket));
        Assert.All(newStore.Locations, location => Assert.Equal(newStore.Options.Bucket, location.Bucket));
        using var final = host.App.Services.CreateScope();
        var db = final.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(2, await db.AudioAssets.CountAsync(Ct));
        Assert.All(await db.AudioAssets.ToListAsync(Ct), asset => Assert.Equal(before.Bucket, asset.Bucket));
    }

    [Fact]
    public async Task InitiallyDisabled_CanEnableAndDisableWhileOldUploadCompletes()
    {
        await using var host = new Host(enabled: false); await host.SeedAsync();
        using var disabled = await Put(host, await host.Revision());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        await host.PublishAsync(options => options.Enabled = true);
        var oldStore = host.Stores.Stores.Last();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldStore.BeforePut = async token => { started.TrySetResult(); await finish.Task.WaitAsync(token); };
        var old = Put(host, await host.Revision());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await host.PublishAsync(options => options.Enabled = false);
        using var rejected = await Put(host, await host.Revision("en-US"), path: "/admin/vocabulary/w/audio/en-US");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(0, oldStore.Disposals);
        finish.TrySetResult();
        using var response = await old;
        await Asset(response);
        Assert.Equal(1, oldStore.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrTimedOutParsing_ReleasesReferenceWithoutReservation(bool timeout)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host(timeout: 1, beforeRead: async token =>
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        await host.SeedAsync();
        using var cancel = new CancellationTokenSource();
        var request = Put(host, await host.Revision(), token: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        if (timeout) { using var response = await request; Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); }
        else { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); }
        // The reference is released by the server finally, which can lag client cancellation.
        for (var attempt = 0; ; attempt++)
        {
            try { await host.PublishAsync(options => options.Bucket = "next-bucket"); break; }
            catch (ConfigurationChangeConflictException) when (attempt < 100) { await Task.Delay(10, Ct); }
        }
        Assert.Equal("next-bucket", host.Runtime.CopyCurrentOptions().Bucket);
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Empty(await db.AudioAssets.ToListAsync(Ct));
        Assert.Empty(await db.AudioUploadOperations.ToListAsync(Ct));
    }

    [Fact]
    public async Task NonCooperativeTimedOutPut_KeepsClientUntilActualProviderCompletion()
    {
        await using var host = new Host(timeout: 1); await host.SeedAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Store.BeforePut = async _ =>
        {
            started.TrySetResult(); await finish.Task;
            ended.TrySetResult(); throw new IOException("synthetic provider completion");
        };
        var request = Put(host, await host.Revision());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await host.PublishAsync(options => options.SecretKey = "synthetic-next-secret");
        using var response = await request;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, host.Store.Disposals);
        await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => host.PublishAsync(options => options.Bucket = "next-bucket"));
        finish.TrySetResult(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        for (var i = 0; i < 100 && host.Store.Disposals == 0; i++) await Task.Delay(10, Ct);
        Assert.Equal(1, host.Store.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnboundHistoricalOrUnpublishedAsset_RejectsDestinationInProductionGuard(bool published)
    {
        await using var host = new Host(); await host.SeedAsync();
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.AudioAssets.Add(new()
            {
                Id = Guid.NewGuid().ToString(),
                Bucket = "lexarbor-audio",
                ObjectKey = $"audio/{Guid.NewGuid():N}.mp3",
                Sha256 = new string('a', 64),
                ByteLength = Mp3.Length,
                CreatedAtUnixMs = 1,
                FirstPublishedAtUnixMs = published ? 2 : null
            });
            await db.SaveChangesAsync(Ct);
            Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Ct));
        }
        await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => host.PublishAsync(options => options.KeyPrefix = "new/"));
        Assert.Equal("audio/", host.Runtime.CopyCurrentOptions().KeyPrefix);
        Assert.Equal(1, host.Stores.Stores.Last().Disposals);
        Assert.Equal(0, host.Store.Calls);
    }

    [Fact]
    public async Task DestinationCommittedBeforeFirstUpload_ReservesAndPutsAtNewLocation()
    {
        await using var host = new Host(); await host.SeedAsync();
        await host.PublishAsync(options =>
        {
            options.InternalEndpoint = "http://127.0.0.1:19/";
            options.Bucket = "new-bucket";
            options.KeyPrefix = "new/";
        });
        using var response = await Put(host, await host.Revision());
        await Asset(response);
        var store = host.Stores.Stores.Last();
        Assert.Equal("http://127.0.0.1:19/", store.Options.InternalEndpoint);
        var location = Assert.Single(store.Locations);
        Assert.Equal("new-bucket", location.Bucket);
        Assert.StartsWith("new/", location.Key);
        using var scope = host.App.Services.CreateScope();
        var asset = await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AudioAssets.SingleAsync(Ct);
        Assert.Equal(location.Bucket, asset.Bucket);
        Assert.Equal(location.Key, asset.ObjectKey);
    }

    [Fact]
    public async Task InitiallyDisabled_ValidOfflineAdvancedAdmissionLimitsSurviveEnablement()
    {
        await using var host = new Host(enabled: false, concurrency: 3, queue: 0); await host.SeedAsync();
        await host.PublishAsync(options => options.Enabled = true);
        var admission = host.App.Services.GetRequiredService<AudioUploadAdmission>();
        using var one = await admission.EnterAsync(Ct);
        using var two = await admission.EnterAsync(Ct);
        using var three = await admission.EnterAsync(Ct);
        Assert.NotNull(one); Assert.NotNull(two); Assert.NotNull(three);
        using var extra = await Put(host, await host.Revision());
        Assert.Equal(HttpStatusCode.TooManyRequests, extra.StatusCode);
    }

    [Fact]
    public async Task HostAdmission_RemainsSharedAcrossGenerationsWithTwoActiveAndEightQueued()
    {
        await using var host = new Host(); await host.SeedAsync();
        var admission = host.App.Services.GetRequiredService<AudioUploadAdmission>();
        var arrivals = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        host.Store.BeforePut = async token => { if (Interlocked.Increment(ref active) == 2) arrivals.TrySetResult(); await release.Task.WaitAsync(token); };
        var revision = await host.Revision();
        var first = Put(host, revision);
        var second = Put(host, revision);
        await arrivals.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = new List<Task<AudioUploadAdmission.Permit?>>();
        // Take request references from successive generations while all generations use the same admission.
        var references = new List<AudioOperationReference>();
        for (var i = 0; i < 8; i++)
        {
            await host.PublishAsync(options => options.SecretKey = $"synthetic-secret-{i}");
            references.Add(await host.Runtime.AcquireAsync(Ct));
            Assert.Same(admission, host.App.Services.GetRequiredService<AudioUploadAdmission>());
            queued.Add(admission.EnterAsync(Ct));
        }
        using var excess = await Put(host, revision);
        Assert.Equal(HttpStatusCode.TooManyRequests, excess.StatusCode);
        Assert.Equal("1", excess.Headers.RetryAfter!.ToString());
        Assert.Equal(2, host.Store.Calls);
        release.TrySetResult();
        using var one = await first; using var two = await second;
        Assert.Equal(new[] { 200, 409 }, new[] { (int)one.StatusCode, (int)two.StatusCode }.Order().ToArray());
        foreach (var waiting in queued) { using var permit = await waiting; Assert.NotNull(permit); }
        foreach (var reference in references) reference.Dispose();
    }

    [Fact]
    public async Task BoundedQueue_WaitsExpiresAndReleasesCancelledWaiters()
    {
        var admission = new AudioUploadAdmission(1, 1, TimeSpan.FromMilliseconds(50));
        using var first = await admission.EnterAsync(Ct); Assert.NotNull(first);
        var waiting = admission.EnterAsync(Ct);
        Assert.Null(await admission.EnterAsync(Ct)); Assert.Null(await waiting);
        using var cancel = new CancellationTokenSource(); var cancelled = admission.EnterAsync(cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        first.Dispose(); using var next = await admission.EnterAsync(Ct); Assert.NotNull(next);
    }

}
