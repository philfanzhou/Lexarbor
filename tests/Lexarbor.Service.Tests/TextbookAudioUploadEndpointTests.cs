using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class TextbookAudioUploadEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Root = "/admin/vocabulary-books/book/units/unit/audio-tracks";
    private const string Version = "00000000-0000-0000-0000-000000000001";
    private static byte[] Mp3 => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    private sealed class Store : IAudioObjectStore, IDisposable
    {
        public int Calls, Disposals, Active, Peak;
        public readonly ConcurrentQueue<AudioUploadPurpose> Purposes = new();
        public Func<CancellationToken, Task>? BeforePut;
        public readonly ConcurrentDictionary<string, byte[]> Objects = new();
        public void Dispose() => Interlocked.Increment(ref Disposals);
        public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token)
            => PutAsync(location, content, length, type, AudioUploadPurpose.Vocabulary, token);
        public async Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, AudioUploadPurpose purpose, CancellationToken token)
        {
            Assert.Equal(0, Disposals); Purposes.Enqueue(purpose); Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active); Peak = Math.Max(Peak, active);
            try
            {
                if (BeforePut != null) await BeforePut(token);
                using var output = new MemoryStream(); await content.CopyToAsync(output, CancellationToken.None);
                Assert.Equal(0, Disposals); Assert.Equal(length, output.Length); Assert.Equal("audio/mpeg", type);
                Objects[location.Key] = output.ToArray();
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => Task.FromResult<AudioObjectInfo?>(null);
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => Task.CompletedTask;
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Stores(Store first) : IAudioObjectStoreFactory
    {
        public List<Store> All = [];
        public IAudioObjectStore Create(AudioOptions options) { var store = All.Count == 0 ? first : new Store(); All.Add(store); return store; }
    }
    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }
        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public Store Store = new();
        public Stores Stores;
        public Logs Logs = new();
        public VocabularyWebApplicationFactory Factory;
        public WebApplicationFactory<Program> App;
        public HttpClient Client;
        public AudioRuntimeConfiguration Runtime => App.Services.GetRequiredService<AudioRuntimeConfiguration>();
        public Host(bool enabled = true, long bytes = 67108864, int seconds = 7200, int concurrency = 2, int queue = 8,
            int timeout = 60, string? database = null, string? crash = null, TimeProvider? clock = null,
            Func<CancellationToken, Task>? reserve = null, Func<CancellationToken, Task>? read = null, ITextbookMp3Validator? validator = null, long wordBytes = 5 * 1024 * 1024)
        {
            Stores = new(Store);
            Factory = new("Testing", true, databasePath: database, keyContentRoot: database is null ? null : Path.Combine(Path.GetDirectoryName(database)!, "keys"), extraConfiguration: new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = enabled.ToString(),
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:PublicEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:AccessKey"] = "synthetic",
                ["Audio:SecretKey"] = "synthetic",
                ["Audio:MaxTextbookUploadBytes"] = bytes.ToString(),
                ["Audio:MaxUploadBytes"] = wordBytes.ToString(),
                ["Audio:MaxTextbookDurationSeconds"] = seconds.ToString(),
                ["Audio:MaxConcurrentUploads"] = concurrency.ToString(),
                ["Audio:MaxQueuedUploads"] = queue.ToString(),
                ["Audio:OperationTimeoutSeconds"] = timeout.ToString()
            });
            App = Factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(Logs)).ConfigureServices(services =>
            {
                services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(Stores);
                if (clock != null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
                if (read != null) services.AddSingleton<IStartupFilter>(new ReadBarrier(read));
                if (validator != null) { services.RemoveAll<ITextbookMp3Validator>(); services.AddSingleton(validator); }
                if (crash != null || reserve != null)
                {
                    services.RemoveAll<ITextbookAudioUploadRepository>();
                    services.AddScoped(provider => Decorate(provider.GetRequiredService<VocabularyDbContext>(), crash, reserve, clock));
                }
            }));
            Client = App.CreateHttpsClient(); Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        public async Task SeedAsync()
        {
            await Db(async db =>
            {
                db.VocabularyBooks.AddRange(new() { Id = "book", BookName = "Book", Status = false }, new() { Id = "other", BookName = "Other" });
                db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
                db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "book", UnitId = "unit", Title = "Original", DisplayOrder = 7, Version = Version });
                db.Vocabularies.Add(new() { Id = "word", Word = "tone" });
                await db.SaveChangesAsync(Ct);
            });
        }
        public async Task Db(Func<VocabularyDbContext, Task> action)
        { using var scope = App.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<VocabularyDbContext>()); }
        public async Task Publish(Action<AudioOptions> change)
        {
            var options = Runtime.CopyCurrentOptions(); change(options);
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }));
            var configuration = new ConfigurationBuilder().AddJsonStream(input).Build();
            using var prepared = await Runtime.PrepareAsync(configuration, Ct); Assert.NotNull(prepared); prepared.Publish();
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
    private static ITextbookAudioUploadRepository Decorate(VocabularyDbContext db, string? crash, Func<CancellationToken, Task>? reserve, TimeProvider? clock)
    {
        var actual = new TextbookAudioUploadRepository(db, clock); var mock = new Mock<ITextbookAudioUploadRepository>();
        mock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string k, CancellationToken t) => actual.GetAsync(k, t));
        mock.Setup(r => r.ReserveAsync(It.IsAny<TextbookAudioUploadRequest>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(async (TextbookAudioUploadRequest r, string o, long n, long e, CancellationToken t) =>
            { if (reserve != null) await reserve(t); return await actual.ReserveAsync(r, o, n, e, t); });
        mock.Setup(r => r.BeginTransferAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((string k, AudioLease l, long n, CancellationToken t) => actual.BeginTransferAsync(k, l, n, t));
        mock.Setup(r => r.CompleteAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(async (string k, AudioLease l, long n, CancellationToken t) =>
            {
                if (crash == null) return await actual.CompleteAsync(k, l, n, t);
                if (crash == "after-commit") await actual.CompleteAsync(k, l, n, t);
                throw new IOException("provider-detail-sensitive-marker");
            });
        mock.Setup(r => r.AbandonAsync(It.IsAny<string>(), It.IsAny<AudioLease>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string k, AudioLease l, long n, int s, string e, CancellationToken t) => actual.AbandonAsync(k, l, n, s, e, t));
        mock.Setup(r => r.ExpireAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((string k, long n, CancellationToken t) => actual.ExpireAsync(k, n, t));
        mock.Setup(r => r.FindRecoverableAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TextbookAudioStoredUpload>());
        return mock.Object;
    }
    private sealed class ReadBarrier(Func<CancellationToken, Task> pause) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use(async (context, invoke) => { if (context.Request.Path.Value!.EndsWith("/content", StringComparison.Ordinal) || context.Request.Path.Value.Contains("/audio/", StringComparison.Ordinal)) context.Request.Body = new PausedStream(context.Request.Body, pause); await invoke(context); }); next(app); };
    }
    private sealed class PausedStream(Stream source, Func<CancellationToken, Task>? pause = null) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => source.Read(b, o, c);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { if (pause != null) await pause(token); return await source.ReadAsync(buffer, token); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
    private static MultipartFormDataContent Body(string version = Version, byte[]? bytes = null, bool includeVersion = true)
    { var body = new MultipartFormDataContent(); if (includeVersion) body.Add(new StringContent(version), "expectedVersion"); body.Add(new ByteArrayContent(bytes ?? Mp3), "file", "untrusted.txt"); return body; }
    private static async Task<HttpResponseMessage> Put(Host host, string version = Version, string? key = null, byte[]? bytes = null,
        string path = Root + "/track/content", CancellationToken? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = Body(version, bytes) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await host.Client.SendAsync(request, token ?? Ct);
    }
    private static void Headers(HttpResponseMessage response)
    {
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)); return json.RootElement.GetProperty("data").Clone(); }
    private static async Task Failure(HttpResponseMessage response, int status)
    { Assert.Equal(status, (int)response.StatusCode); Headers(response); Assert.DoesNotContain("provider-detail-sensitive-marker", await response.Content.ReadAsStringAsync(Ct)); }
    private static async Task Empty(Host host)
    { Assert.Equal(0, host.Store.Calls); await host.Db(async db => { Assert.Equal(0, await db.TextbookAudioUploadOperations.CountAsync(Ct)); Assert.Equal(0, await db.AudioAssets.CountAsync(Ct)); }); }

    [Fact]
    public async Task Success_ExactReceiptAndCurrentMetadata_HistoryAndPermanentReplayAfterRestartWithTighterPolicy()
    {
        var directory = VocabularyWebApplicationFactory.CreateGateSafeDirectory("textbook-replay-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.db"); var key = Guid.NewGuid().ToString(); string receipt;
        var longerKey = Guid.NewGuid().ToString(); var longerBytes = TextbookMp3ValidatorTests.SilentFrames(80); string longerReceipt; string longerExpected;
        try
        {
            await using (var host = new Host(database: path))
            {
                await host.SeedAsync();
                using var uploaded = await Put(host, key: key); var data = await Data(uploaded); receipt = data.GetRawText();
                Assert.Equal(new[] { "durationSeconds", "trackId", "version" }, data.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal("track", data.GetProperty("trackId").GetString()); Assert.Equal(25344d / 44100, data.GetProperty("durationSeconds").GetDouble(), 10);
                var currentVersion = data.GetProperty("version").GetString()!; longerExpected = currentVersion;
                await host.Db(async db => await db.TextbookAudioTracks.ExecuteUpdateAsync(s => s.SetProperty(t => t.IsAvailable, true), Ct));
                using var replacement = await Put(host, currentVersion, longerKey, longerBytes); var longerData = await Data(replacement);
                longerReceipt = longerData.GetRawText(); Assert.True(longerData.GetProperty("durationSeconds").GetDouble() > 1);
                await host.Db(async db =>
                {
                    var track = await db.TextbookAudioTracks.SingleAsync(Ct); Assert.False(track.IsAvailable); Assert.Equal("Original", track.Title); Assert.Equal(7, track.DisplayOrder);
                    Assert.Equal(2, await db.AudioAssets.CountAsync(Ct)); Assert.All(await db.AudioAssets.ToListAsync(Ct), a => Assert.NotNull(a.FirstPublishedAtUnixMs));
                    await db.TextbookAudioTracks.ExecuteDeleteAsync(Ct); await db.VocabularyBookUnits.ExecuteDeleteAsync(Ct); await db.VocabularyBooks.ExecuteDeleteAsync(Ct);
                });
                using var replay = await Put(host, key: key); Assert.Equal(receipt, (await Data(replay)).GetRawText()); Assert.Equal(2, host.Store.Calls);
            }
            var rejects = new Mock<ITextbookMp3Validator>(MockBehavior.Strict);
            await using (var restarted = new Host(database: path, bytes: 1, seconds: 1, validator: rejects.Object))
            {
                using var replay = await Put(restarted, key: key); Assert.Equal(receipt, (await Data(replay)).GetRawText());
                using var longerReplay = await Put(restarted, longerExpected, longerKey, longerBytes); Assert.Equal(longerReceipt, (await Data(longerReplay)).GetRawText());
                using var changedVersion = await Put(restarted, Guid.NewGuid().ToString(), key); await Failure(changedVersion, 409);
                var changedBytes = Mp3; changedBytes[^1] ^= 1;
                using var changed = await Put(restarted, key: key, bytes: changedBytes); await Failure(changed, 409);
                using var size = await Put(restarted, key: key, bytes: Mp3.Concat(new byte[1]).ToArray()); await Failure(size, 409);
                using var target = await Put(restarted, key: key, path: Root + "/missing/content"); await Failure(target, 409);
                using var fresh = await Put(restarted); await Failure(fresh, 413);
                await restarted.Publish(o => o.Enabled = false);
                using var disabled = await Put(restarted, key: key); await Failure(disabled, 503);
                rejects.VerifyNoOtherCalls(); Assert.Equal(0, restarted.Store.Calls);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentUploadLimits_RejectOnlyOversizedTypeBeforeReserveAndPreserveCurrent(bool textbookLarger)
    {
        var size = Mp3.Length;
        await using var host = new Host(bytes: textbookLarger ? size : size - 1, wordBytes: textbookLarger ? size - 1 : size);
        await host.SeedAsync();
        await host.Db(async db =>
        {
            db.AudioAssets.Add(new()
            {
                Id = "prior",
                Bucket = "lexarbor-audio",
                ObjectKey = "audio/" + Guid.NewGuid().ToString("N") + ".mp3",
                Sha256 = new string('a', 64),
                ByteLength = 1,
                FirstPublishedAtUnixMs = 1
            });
            await db.SaveChangesAsync(Ct);
            await db.TextbookAudioTracks.ExecuteUpdateAsync(s => s.SetProperty(t => t.CurrentAssetId, "prior").SetProperty(t => t.DurationSeconds, 12).SetProperty(t => t.IsAvailable, true), Ct);
        });
        using var policy = await host.Client.GetAsync(Root + "/upload-policy", Ct);
        Assert.Equal(textbookLarger ? size : size - 1, (await Data(policy)).GetProperty("maxUploadBytes").GetInt64());
        using var textbook = await Put(host);
        if (textbookLarger) await Data(textbook); else await Failure(textbook, 413);
        string? revision = null;
        using (var scope = host.App.Services.CreateScope())
        {
            var snapshot = await scope.ServiceProvider.GetRequiredService<IAudioReadRepository>().GetSnapshotAsync("word", null, Ct);
            revision = AudioSelectionService.Select(snapshot, AudioLocale.EnGb).SourceRevision;
        }
        using var body = new MultipartFormDataContent(); body.Add(new StringContent(revision), "sourceRevision");
        body.Add(new StringContent(""), "expectedAssetId"); body.Add(new ByteArrayContent(Mp3), "file", "tone.mp3");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/vocabulary/word/audio/en-GB") { Content = body };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var word = await host.Client.SendAsync(request, Ct);
        Assert.Equal(textbookLarger ? 413 : 200, (int)word.StatusCode);
        Assert.Equal(new[] { textbookLarger ? AudioUploadPurpose.Textbook : AudioUploadPurpose.Vocabulary }, host.Store.Purposes);
        await host.Db(async db =>
        {
            Assert.Equal(textbookLarger ? 1 : 0, await db.TextbookAudioUploadOperations.CountAsync(Ct));
            Assert.Equal(textbookLarger ? 0 : 1, await db.AudioUploadOperations.CountAsync(Ct));
            Assert.Equal(2, await db.AudioAssets.CountAsync(Ct));
            var track = await db.TextbookAudioTracks.SingleAsync(Ct);
            if (!textbookLarger) { Assert.Equal(Version, track.Version); Assert.Equal(12, track.DurationSeconds); Assert.True(track.IsAvailable); Assert.Equal("prior", track.CurrentAssetId); }
        });
    }

    [Fact]
    public async Task StoredLegacyStorageFailure_ReplaysUnchangedAfterRestartWithCorrectedLimits()
    {
        var directory = VocabularyWebApplicationFactory.CreateGateSafeDirectory("textbook-failed-replay-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.db"); var key = Guid.NewGuid().ToString(); string failedReceipt, expected, current;
        try
        {
            await using (var host = new Host(database: path, wordBytes: 1))
            {
                await host.SeedAsync(); using var first = await Put(host); expected = (await Data(first)).GetProperty("version").GetString()!;
                current = ""; await host.Db(async db => current = (await db.TextbookAudioTracks.SingleAsync(Ct)).CurrentAssetId!);
                host.Store.BeforePut = _ => throw new AudioStorageException("audio.storage.invalid_input", "Audio object input is invalid.");
                using var failed = await Put(host, expected, key); await Failure(failed, 503); failedReceipt = await failed.Content.ReadAsStringAsync(Ct);
                Assert.All(host.Store.Purposes, p => Assert.Equal(AudioUploadPurpose.Textbook, p));
            }
            await using (var restarted = new Host(database: path, bytes: Mp3.Length, wordBytes: 1))
            {
                using var replay = await Put(restarted, expected, key); await Failure(replay, 503);
                Assert.Equal(failedReceipt, await replay.Content.ReadAsStringAsync(Ct)); Assert.Equal(0, restarted.Store.Calls);
                await restarted.Db(async db =>
                {
                    var track = await db.TextbookAudioTracks.SingleAsync(Ct); Assert.Equal(current, track.CurrentAssetId); Assert.Equal(expected, track.Version);
                    Assert.Equal(2, await db.AudioAssets.CountAsync(Ct)); Assert.Equal(1, await db.AudioAssets.CountAsync(a => a.FirstPublishedAtUnixMs != null, Ct));
                    var old = (await new TextbookAudioUploadRepository(db).GetAsync(key, Ct))!;
                    Assert.Equal(TextbookAudioUploadService.Fingerprint("book", "unit", "track", expected, old.Asset.Sha256, Mp3.Length), old.Operation.RequestFingerprint);
                    Assert.Equal(503, old.Operation.Receipt!.StatusCode);
                });
                using var fresh = await Put(restarted, expected); await Data(fresh); Assert.Equal(new[] { AudioUploadPurpose.Textbook }, restarted.Store.Purposes);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Policy_ReturnsBoundedSameGenerationLimitsWithoutStorageOrTrack(bool enabled)
    {
        await using var host = new Host(enabled, bytes: 12345, seconds: 37); await host.SeedAsync();
        using var policy = await host.Client.GetAsync(Root + "/upload-policy", Ct); var data = await Data(policy);
        Assert.Equal(new[] { "enabled", "maxDurationSeconds", "maxUploadBytes" }, data.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(enabled, data.GetProperty("enabled").GetBoolean()); Assert.Equal(12345, data.GetProperty("maxUploadBytes").GetInt64()); Assert.Equal(37, data.GetProperty("maxDurationSeconds").GetInt32());
        using var missing = await host.Client.GetAsync(Root.Replace("book/", "missing/", StringComparison.Ordinal) + "/upload-policy", Ct); await Failure(missing, 404);
        using var wrong = await host.Client.GetAsync(Root.Replace("book/", "other/", StringComparison.Ordinal) + "/upload-policy", Ct); await Failure(wrong, 409);
        await Empty(host);
    }

    [Theory]
    [InlineData("fake", 415)]
    [InlineData("truncated", 415)]
    [InlineData("corrupt", 415)]
    [InlineData("version", 400)]
    [InlineData("missing", 400)]
    [InlineData("uppercase", 400)]
    [InlineData("duplicate", 400)]
    [InlineData("multi", 400)]
    [InlineData("duration", 400)]
    [InlineData("bucket", 400)]
    [InlineData("key", 400)]
    [InlineData("url", 400)]
    [InlineData("malformed", 400)]
    [InlineData("bytes", 413)]
    [InlineData("seconds", 422)]
    [InlineData("target", 404)]
    [InlineData("owner", 409)]
    [InlineData("stale", 409)]
    public async Task InvalidInputOrTarget_NeverReserveOrPut(string kind, int status)
    {
        await using var host = new Host(bytes: kind == "bytes" ? 100 : 67108864, seconds: kind == "seconds" ? 1 : 7200); await host.SeedAsync();
        var bytes = kind switch { "fake" => "not mp3"u8.ToArray(), "truncated" => Mp3[..^1], "seconds" => TextbookMp3ValidatorTests.SilentFrames(40), _ => Mp3 };
        if (kind == "corrupt") Array.Fill(bytes, (byte)255, TextbookMp3ValidatorTests.FrameOffsets(bytes)[2] + 4, 17);
        using var body = Body(kind == "version" ? "bad" : kind == "stale" ? Guid.NewGuid().ToString() : Version, bytes, kind != "missing");
        if (kind == "duplicate") body.Add(new StringContent(Version), "expectedVersion");
        if (kind == "multi") body.Add(new ByteArrayContent(Mp3), "file", "extra.mp3");
        if (kind is "duration" or "bucket" or "key" or "url") body.Add(new StringContent("untrusted"), kind);
        var path = kind == "target" ? Root + "/missing/content" : kind == "owner" ? Root.Replace("book/", "other/", StringComparison.Ordinal) + "/track/content" : Root + "/track/content";
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = kind == "malformed" ? new StringContent("not multipart") : body };
        request.Headers.Add("Idempotency-Key", kind == "uppercase" ? "AAAAAAAA-0000-0000-0000-000000000001" : Guid.NewGuid().ToString());
        using var response = await host.Client.SendAsync(request, Ct); await Failure(response, status); await Empty(host);
    }

    [Theory]
    [InlineData("missing-key", 400)]
    [InlineData("multiple-key", 400)]
    [InlineData("boundary", 400)]
    [InlineData("headers", 400)]
    [InlineData("header-bytes", 400)]
    [InlineData("text", 400)]
    [InlineData("chunked-file", 413)]
    [InlineData("epilogue", 413)]
    [InlineData("preamble", 413)]
    public async Task MultipartAndUnknownLengthLimits_AreEnforced(string kind, int status)
    {
        await using var host = new Host(bytes: kind == "chunked-file" ? 100 : 1024 * 1024); await host.SeedAsync();
        using var original = Body(); var bytes = await original.ReadAsByteArrayAsync(Ct); var type = original.Headers.ContentType!.ToString();
        if (kind == "epilogue") bytes = bytes.Concat(new byte[65537]).ToArray();
        if (kind == "preamble") bytes = Enumerable.Repeat((byte)32, 65537).Concat("\r\n"u8.ToArray()).Concat(bytes).ToArray();
        if (kind is "headers" or "header-bytes" or "text")
        {
            var boundary = original.Headers.ContentType.Parameters.First().Value!.Trim('"');
            var headers = kind == "headers" ? string.Concat(Enumerable.Range(0, 9).Select(i => $"X-{i}: value\r\n")) : kind == "header-bytes" ? "X: " + new string('a', 8193) + "\r\n" : "";
            bytes = Encoding.UTF8.GetBytes($"--{boundary}\r\n{headers}Content-Disposition: form-data; name=\"expectedVersion\"\r\n\r\n{new string('v', kind == "text" ? 37 : 36)}\r\n--{boundary}--\r\n");
        }
        using var content = new StreamContent(new PausedStream(new MemoryStream(bytes))); content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        if (kind == "boundary") content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + new string('a', 129));
        using var request = new HttpRequestMessage(HttpMethod.Put, Root + "/track/content") { Content = content };
        if (kind != "missing-key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (kind == "multiple-key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await host.Client.SendAsync(request, Ct); await Failure(response, status); await Empty(host);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie", 401)]
    [InlineData("mixed", 403)]
    public async Task AuthorizationAndCsrf_PrecedeBodyAndKeepBearerPriority(string identity, int status)
    {
        var reads = 0;
        await using var host = new Host(read: _ => { Interlocked.Increment(ref reads); return Task.CompletedTask; }); await host.SeedAsync();
        host.Client.DefaultRequestHeaders.Authorization = identity is "student" or "mixed" ? new("Bearer", host.Factory.CreateToken("student")) : null;
        if (identity is "cookie" or "mixed") host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("admin"));
        using var response = await Put(host, bytes: "invalid"u8.ToArray()); await Failure(response, status);
        Assert.Equal(0, reads); await Empty(host);
        if (identity != "cookie") { using var policy = await host.Client.GetAsync(Root + "/upload-policy", Ct); await Failure(policy, status); }
    }
    [Fact]
    public async Task CookieWithCsrf_AndBearerWithCookie_AreAccepted()
    {
        await using var host = new Host(); await host.SeedAsync(); var cookie = host.Factory.CreateSessionCookie("admin");
        host.Client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var first = await Put(host); var version = (await Data(first)).GetProperty("version").GetString()!;
        host.Client.DefaultRequestHeaders.Authorization = null;
        AdminTestAntiforgery.Attach(host.Client, host.App);
        using var second = await Put(host, version); await Data(second);
    }

    [Fact]
    public async Task InProgressSameKeyAndConcurrentReplace_OnlyOneCurrentFileCanPublish()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host(); await host.SeedAsync();
        host.Store.BeforePut = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var key = Guid.NewGuid().ToString(); var first = Put(host, key: key); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var inProgress = await Put(host, key: key); await Failure(inProgress, 409); Assert.Equal(1, host.Store.Calls);
        var competing = Put(host); for (var i = 0; i < 100 && host.Store.Calls < 2; i++) await Task.Delay(10, Ct);
        Assert.Equal(2, host.Store.Calls); release.SetResult();
        using var a = await first; using var b = await competing; Assert.Equal(new[] { 200, 409 }, new[] { (int)a.StatusCode, (int)b.StatusCode }.Order());
        await host.Db(async db =>
        {
            Assert.Equal(2, await db.AudioAssets.CountAsync(Ct)); Assert.Equal(1, await db.AudioAssets.CountAsync(x => x.FirstPublishedAtUnixMs != null, Ct));
            Assert.Equal(1, await db.TextbookAudioTracks.CountAsync(x => x.CurrentAssetId != null && !x.IsAvailable, Ct));
        });
    }

    [Theory]
    [InlineData(false, 409)]
    [InlineData(true, 404)]
    public async Task EditOrDeleteDuringPut_PreservesPriorFileAndReplaysFailure(bool delete, int status)
    {
        await using var host = new Host(); await host.SeedAsync(); using var first = await Put(host); var version = (await Data(first)).GetProperty("version").GetString()!;
        string? original = null; await host.Db(async db => original = (await db.TextbookAudioTracks.SingleAsync(Ct)).CurrentAssetId);
        host.Store.BeforePut = async _ => await host.Db(async db =>
        {
            if (delete) await db.TextbookAudioTracks.ExecuteDeleteAsync(Ct);
            else await db.TextbookAudioTracks.ExecuteUpdateAsync(s => s.SetProperty(t => t.Title, "Edited").SetProperty(t => t.Version, Guid.NewGuid().ToString()), Ct);
        });
        var key = Guid.NewGuid().ToString(); using var failed = await Put(host, version, key); await Failure(failed, status);
        using var replay = await Put(host, version, key); await Failure(replay, status); Assert.Equal(2, host.Store.Calls);
        await host.Db(async db =>
        {
            Assert.Equal(2, await db.AudioAssets.CountAsync(Ct)); Assert.Equal(1, await db.AudioAssets.CountAsync(a => a.FirstPublishedAtUnixMs != null, Ct));
            if (!delete) { var track = await db.TextbookAudioTracks.SingleAsync(Ct); Assert.Equal(original, track.CurrentAssetId); Assert.Equal("Edited", track.Title); }
        });
    }

    [Theory]
    [InlineData("after-put", 409, AudioUploadState.Transferring)]
    [InlineData("after-commit", 200, AudioUploadState.Succeeded)]
    public async Task UnknownCommitOrLostResponse_PreservesOriginalKeyResultAndRecovery(string point, int replay, AudioUploadState state)
    {
        var clock = new Clock();
        await using var host = new Host(crash: point, clock: clock); await host.SeedAsync(); var key = Guid.NewGuid().ToString();
        using var lost = await Put(host, key: key); await Failure(lost, 503);
        using var retry = await Put(host, key: key); Assert.Equal(replay, (int)retry.StatusCode); Assert.Equal(1, host.Store.Calls);
        await host.Db(async db =>
        {
            var operation = await db.TextbookAudioUploadOperations.SingleAsync(Ct); Assert.Equal(state, operation.State);
            Assert.Equal(point == "after-commit", (await db.TextbookAudioTracks.SingleAsync(Ct)).CurrentAssetId != null);
            if (point == "after-put")
            {
                clock.Milliseconds += 300001;
                await new AudioRecoveryService(new TextbookAudioUploadRepository(db, clock), host.Store, new(true, "lexarbor-audio", "audio/"), clock).RunBatchAsync(Ct);
                Assert.Equal("audio.upload_expired", (await new TextbookAudioUploadRepository(db, clock).GetAsync(key, Ct))!.Operation.Receipt!.ErrorCode);
            }
        });
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("provider-detail-sensitive-marker", StringComparison.Ordinal));
        if (point == "after-put") { using var expired = await Put(host, key: key); await Failure(expired, 503); }
    }
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Milliseconds);
    }

    [Theory]
    [InlineData("parse")]
    [InlineData("reserve")]
    [InlineData("put")]
    public async Task CredentialRotationAndDisable_DuringEveryBoundary_UseCapturedGeneration(string phase)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        async Task Pause(CancellationToken token) { if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); } }
        await using var host = new Host(reserve: phase == "reserve" ? Pause : null, read: phase == "parse" ? Pause : null); await host.SeedAsync();
        if (phase == "put") host.Store.BeforePut = Pause;
        var pending = Put(host); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => host.Publish(o => o.Bucket = "other-bucket"));
        await host.Publish(o => { o.SecretKey = "synthetic-rotated"; o.Enabled = false; });
        Assert.Equal(0, host.Store.Disposals);
        using var policy = await host.Client.GetAsync(Root + "/upload-policy", Ct); Assert.False((await Data(policy)).GetProperty("enabled").GetBoolean());
        using var rejected = await Put(host); await Failure(rejected, 503);
        release.SetResult(); using var finished = await pending; await Data(finished);
        Assert.Equal(new[] { AudioUploadPurpose.Textbook }, host.Store.Purposes);
        Assert.Equal(1, host.Store.Calls); Assert.Equal(1, host.Store.Disposals); Assert.Equal(0, host.Stores.All[^1].Calls);
    }

    [Fact]
    public async Task SharedVocabularyAdmission_BoundsBothTypesDuringReadAndPut()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host(concurrency: 1, queue: 0, read: async t => { entered.TrySetResult(); await release.Task.WaitAsync(t); }); await host.SeedAsync();
        var pending = Put(host); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var textbook = await Put(host); await Failure(textbook, 429); Assert.Equal("1", textbook.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        using var wordBody = new MultipartFormDataContent(); wordBody.Add(new StringContent("bad"), "sourceRevision");
        using var wordRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/vocabulary/word/audio/en-GB") { Content = wordBody }; wordRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var word = await host.Client.SendAsync(wordRequest, Ct); await Failure(word, 429);
        release.SetResult(); using var success = await pending; await Data(success);
        // The inverse direction uses the very same singleton permit too.
        using var permit = await host.App.Services.GetRequiredService<AudioUploadAdmission>().EnterAsync(Ct);
        using var again = await Put(host); await Failure(again, 429);
    }

    [Theory]
    [InlineData("parse")]
    [InlineData("media")]
    [InlineData("put")]
    public async Task TimeoutBeforeReserveOrDuringPut_IsSafeAndReleasesAdmission(string phase)
    {
        var reject = new Mock<ITextbookMp3Validator>(); reject.Setup(x => x.Validate(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Throws(new TimeoutException("provider-detail-sensitive-marker"));
        await using var host = new Host(timeout: 1, read: phase == "parse" ? t => Task.Delay(Timeout.Infinite, t) : null, validator: phase == "media" ? reject.Object : null); await host.SeedAsync();
        if (phase == "put") host.Store.BeforePut = t => Task.Delay(Timeout.Infinite, t);
        using var response = await Put(host); await Failure(response, 503);
        await host.Db(async db =>
        {
            Assert.Null((await db.TextbookAudioTracks.SingleAsync(Ct)).CurrentAssetId);
            if (phase == "put") Assert.Equal(503, (await db.TextbookAudioUploadOperations.SingleAsync(Ct)).ReceiptStatusCode);
            else Assert.Empty(await db.TextbookAudioUploadOperations.ToListAsync(Ct));
        });
        using var permit = await host.App.Services.GetRequiredService<AudioUploadAdmission>().EnterAsync(Ct); Assert.NotNull(permit);
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("provider-detail-sensitive-marker", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IgnoredCancellation_RetainsSharedCapacityClientAndStreamUntilActualPutEnds_AndCannotPublish(bool disconnect, bool lateFailure)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0; var reservations = 0;
        await using var host = new Host(timeout: disconnect ? 60 : 1, concurrency: 1, queue: 0,
            read: _ => { Interlocked.Increment(ref reads); return Task.CompletedTask; },
            reserve: _ => { Interlocked.Increment(ref reservations); return Task.CompletedTask; });
        await host.SeedAsync();
        host.Store.BeforePut = async _ =>
        {
            entered.TrySetResult(); await release.Task;
            if (lateFailure) throw new IOException("provider-detail-sensitive-marker");
        };
        using var cancel = new CancellationTokenSource();
        var key = Guid.NewGuid().ToString(); var pending = Put(host, key: key, token: cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await host.Publish(o => o.SecretKey = "synthetic-rotated");
            if (disconnect)
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            }
            else { using var timeout = await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct); await Failure(timeout, 503); }
            // Client cancellation can precede the server's durable abandonment.
            int? receipt = null;
            for (var i = 0; i < 500 && receipt is null; i++)
            {
                await host.Db(async db => receipt = (await db.TextbookAudioUploadOperations.SingleAsync(Ct)).ReceiptStatusCode);
                if (receipt is null) await Task.Delay(10, Ct);
            }
            Assert.Equal(503, receipt); Assert.Equal(0, host.Store.Disposals); Assert.Equal(1, host.Store.Active);
            var readCount = reads;
            async Task RejectBothTypes()
            {
                using var textbook = await Put(host); await Failure(textbook, 429);
                Assert.Equal(TimeSpan.FromSeconds(1), textbook.Headers.RetryAfter!.Delta);
                using var wordBody = new MultipartFormDataContent(); wordBody.Add(new StringContent("bad"), "sourceRevision");
                using var wordRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/vocabulary/word/audio/en-GB") { Content = wordBody };
                wordRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                using var word = await host.Client.SendAsync(wordRequest, Ct); await Failure(word, 429);
                Assert.Equal(TimeSpan.FromSeconds(1), word.Headers.RetryAfter!.Delta);
                Assert.Equal(readCount, reads); Assert.Equal(1, reservations);
            }
            // Repeated new keys cannot accumulate actual IO or encoded file buffers.
            await RejectBothTypes(); await RejectBothTypes();
            Assert.Equal(new[] { AudioUploadPurpose.Textbook }, host.Store.Purposes);
            Assert.Equal(1, host.Store.Calls); Assert.Equal(1, host.Store.Peak);
            await host.Db(async db => Assert.Single(await db.TextbookAudioUploadOperations.ToListAsync(Ct)));
            release.SetResult();
            var admission = host.App.Services.GetRequiredService<AudioUploadAdmission>();
            IDisposable? recovered = null;
            for (var i = 0; i < 500 && recovered is null; i++)
            {
                recovered = await admission.EnterAsync(Ct);
                if (recovered is null) await Task.Delay(10, Ct);
            }
            using (recovered)
            {
                Assert.NotNull(recovered); Assert.Equal(0, host.Store.Active); Assert.Equal(1, host.Store.Disposals);
                Assert.Equal(lateFailure ? 0 : 1, host.Store.Objects.Count);
                // An over-release would allow a second request alongside this held slot.
                await RejectBothTypes();
                recovered.Dispose(); recovered.Dispose();
            }
            using var replay = await Put(host, key: key); await Failure(replay, 503); Assert.Equal(1, host.Store.Calls);
            await host.Db(async db =>
            {
                Assert.Null((await db.TextbookAudioTracks.SingleAsync(Ct)).CurrentAssetId);
                Assert.Null((await db.AudioAssets.SingleAsync(Ct)).FirstPublishedAtUnixMs);
            });
            using var success = await Put(host); await Data(success);
            Assert.Equal(1, host.Stores.All[^1].Calls); Assert.Equal(1, host.Store.Peak);
            Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("provider-detail-sensitive-marker", StringComparison.Ordinal));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ClientDisconnect_DuringPut_LeavesDurableFailureWithOriginalKey()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host(); await host.SeedAsync();
        host.Store.BeforePut = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var cancel = new CancellationTokenSource(); var key = Guid.NewGuid().ToString(); var pending = Put(host, key: key, token: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var replay = await Put(host, key: key); await Failure(replay, 503); Assert.Equal(1, host.Store.Calls);
        await host.Db(async db => Assert.Null((await db.TextbookAudioTracks.SingleAsync(Ct)).CurrentAssetId));
    }
    [Theory]
    [InlineData("lookup")]
    [InlineData("reserve")]
    [InlineData("policy")]
    public async Task DatabaseBusy_UsesExistingProblemDetailsAndRetryAfter(string phase)
    {
        await using var host = new Host(reserve: phase == "reserve" ? _ => throw new StorageBusyException("The vocabulary database is busy. Please retry the request.") : null);
        await host.SeedAsync();
        await using var app = host.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            if (phase == "lookup")
            {
                var repo = new Mock<ITextbookAudioUploadRepository>();
                repo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new StorageBusyException("The vocabulary database is busy. Please retry the request."));
                services.RemoveAll<ITextbookAudioUploadRepository>(); services.AddScoped(_ => repo.Object);
            }
            if (phase == "policy")
            {
                var repo = new Mock<ITextbookAudioTrackRepository>();
                repo.Setup(r => r.ReadSnapshotAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new StorageBusyException("The vocabulary database is busy. Please retry the request."));
                services.RemoveAll<ITextbookAudioTrackRepository>(); services.AddScoped(_ => repo.Object);
            }
        }));
        using var client = app.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", host.Factory.CreateToken("admin"));
        using var request = new HttpRequestMessage(phase == "policy" ? HttpMethod.Get : HttpMethod.Put, Root + (phase == "policy" ? "/upload-policy" : "/track/content"));
        if (phase != "policy") { request.Content = Body(); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); }
        // Reserve fault lives in the original decorated host; lookup/policy use the thin fault host.
        using var response = phase == "reserve" ? await Put(host) : await client.SendAsync(request, Ct);
        await Failure(response, 503); Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter!.Delta);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType); await Empty(host);
    }

    [Fact]
    public async Task ProviderException_IsSafeInResponseAndLogs_AndKeepsPriorFile()
    {
        await using var host = new Host(); await host.SeedAsync(); using var first = await Put(host); var version = (await Data(first)).GetProperty("version").GetString()!;
        host.Store.BeforePut = _ => throw new IOException("provider-detail-sensitive-marker");
        var key = Guid.NewGuid().ToString(); using var failed = await Put(host, version, key); await Failure(failed, 503);
        using var replay = await Put(host, version, key); await Failure(replay, 503); Assert.Equal(2, host.Store.Calls);
        await host.Db(async db => { Assert.NotNull((await db.TextbookAudioTracks.SingleAsync(Ct)).CurrentAssetId); Assert.Equal(1, await db.AudioAssets.CountAsync(a => a.FirstPublishedAtUnixMs != null, Ct)); });
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("provider-detail-sensitive-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileFirstAndMimeHints_DoNotReplaceActualContentValidation()
    {
        await using var host = new Host(); await host.SeedAsync();
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(Mp3); file.Headers.ContentType = new("image/png");
        body.Add(file, "file", "wrong.png"); body.Add(new StringContent(Version), "expectedVersion");
        using var request = new HttpRequestMessage(HttpMethod.Put, Root + "/track/content") { Content = body }; request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await host.Client.SendAsync(request, Ct); var version = (await Data(response)).GetProperty("version").GetString()!;
        using var fakeBody = Body(version, "fake MP3"u8.ToArray()); fakeBody.Last().Headers.ContentType = new("audio/mpeg");
        using var fakeRequest = new HttpRequestMessage(HttpMethod.Put, Root + "/track/content") { Content = fakeBody }; fakeRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var fake = await host.Client.SendAsync(fakeRequest, Ct); await Failure(fake, 415); Assert.Equal(1, host.Store.Calls);
    }

}
