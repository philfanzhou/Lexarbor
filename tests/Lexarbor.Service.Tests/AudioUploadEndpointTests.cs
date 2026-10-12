using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class AudioUploadEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] Mp3 => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    private sealed class Store : IAudioObjectStore
    {
        public int Calls;
        public Func<CancellationToken, Task>? BeforePut;
        public readonly Dictionary<string, byte[]> Objects = [];
        public async Task PutAsync(AudioObjectLocation location, Stream stream, long length, string type, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (BeforePut != null) await BeforePut(token);
            using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes, token);
            lock (Objects) Objects.Add(location.Key, bytes.ToArray());
        }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => throw new NotImplementedException();
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new NotImplementedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan ttl, CancellationToken token) => throw new NotImplementedException();
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly Store Store = new();
        public readonly VocabularyWebApplicationFactory Factory;
        public readonly WebApplicationFactory<Program> App;
        public readonly HttpClient Client;
        public Host(bool enabled = true, long maxBytes = 5 * 1024 * 1024, int concurrency = 2, int queue = 8, int timeout = 60, TimeProvider? clock = null, string? crash = null)
        {
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
                if (clock != null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
                if (crash != null)
                {
                    services.RemoveAll<IAudioUploadRepository>();
                    services.AddScoped(provider => FaultRepository(provider.GetRequiredService<VocabularyDbContext>(), crash));
                }
            }));
            Client = App.CreateClient(new() { BaseAddress = new("https://localhost") });
            Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        private static IAudioUploadRepository FaultRepository(VocabularyDbContext db, string crash)
        {
            var actual = new AudioUploadRepository(db);
            var mock = new Mock<IAudioUploadRepository>();
            mock.Setup(repo => repo.ReserveAsync(It.IsAny<AudioUploadRequest>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((AudioUploadRequest request, string owner, long now, long expires, CancellationToken token) => actual.ReserveAsync(request, owner, now, expires, token));
            mock.Setup(repo => repo.BeginTransferAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string key, AudioLease lease, long now, CancellationToken token) => actual.BeginTransferAsync(key, lease, now, token));
            mock.Setup(repo => repo.CompleteAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(async (string key, AudioLease lease, long now, CancellationToken token) =>
                {
                    if (crash == "after-commit") await actual.CompleteAsync(key, lease, now, token);
                    throw new IOException("Injected process/response loss.");
                });
            return mock.Object;
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
