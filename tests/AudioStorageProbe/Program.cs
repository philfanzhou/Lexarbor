using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Lexarbor.Host.Audio;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Lexarbor.Host.Configuration;
using Minio;
using Minio.DataModel.Args;

try
{
    if (args.Length == 4 && args[0] == "--crash-upload")
    {
        await CrashUploadAsync(args[1], args[2], args[3]);
        return 0;
    }
    var endpoint = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_INTERNAL")!;
    var publicEndpoint = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_PUBLIC")!;
    var bucket = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_BUCKET")!;
    var options = new AudioOptions
    {
        Enabled = true,
        InternalEndpoint = endpoint,
        PublicEndpoint = publicEndpoint,
        Bucket = bucket,
        AccessKey = "synthetic-audio-access",
        SecretKey = "synthetic-audio-secret",
        OperationTimeoutSeconds = 3
    };
    using var setup = new MinioClient().WithEndpoint(new Uri(endpoint)).WithCredentials("synthetic-setup-access", "synthetic-setup-secret")
        .WithRegion("us-east-1").WithHttpClient(new HttpClient(), true).Build();
    // Provisioning belongs solely to this disposable test harness, never the app.
    await setup.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
    var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    await SettingsApiProbe.VerifyAsync(options, bytes);
    Console.WriteLine("Private S3 settings API save/upload/playback/disable/re-enable/recovery/restart passed.");
    await VerifyRuntimeUploadGenerationsAsync(options, bytes);
    await VerifyRuntimeRecoveryGenerationsAsync(options, bytes);
    await TextbookRecoveryProbe.VerifyAsync(options, bytes);
    await TextbookContentProbe.VerifyAsync(options, bytes);
    using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
    Require(Mp3Validation.IsValid(bytes), "The uploaded fixture must contain complete MP3 frames.");
    using var databaseRoot = new ProbeDatabaseRoot();
    await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={databaseRoot.Path};Pooling=False").Options);
    await db.Database.MigrateAsync();
    db.Vocabularies.Add(new VocabularyEntity { Id = "probe-word", Word = "tone" });
    db.VocabularyBooks.Add(new() { Id = "probe-book", BookName = "Probe", Status = true });
    db.VocabularyMeanings.Add(new() { Id = "probe-meaning", VocabularyId = "probe-word", BookId = "probe-book", Meaning = "tone" });
    await db.SaveChangesAsync();
    var repository = new AudioUploadRepository(db);
    var coordinator = new AudioUploadService(repository, store, new(true, bucket, "audio/", 3), TimeProvider.System);
    var revision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("probe-word", "tone", null, null), null, AudioLocale.EnGb));
    var request = new AudioUploadContent(Guid.NewGuid().ToString(), "probe-word", null, AudioLocale.EnGb, revision, null, bytes, "probe", null);
    var uploaded = await coordinator.UploadAsync(request, default);
    Require(uploaded.StatusCode == 200, "Conditional upload must publish the real MP3.");
    var persisted = (await repository.GetAsync(request.IdempotencyKey, default))!;
    var location = persisted.Asset.Location;
    Require(persisted.Operation.State == AudioUploadState.Succeeded && persisted.Asset.FirstPublishedAtUnixMs != null,
        "The successful upload must have a durable receipt and published stamp.");
    Require((await coordinator.UploadAsync(request, default)) == uploaded && await db.AudioAssets.CountAsync() == 1,
        "An identical retry must return the original receipt without a second object.");
    var stat = await store.StatAsync(location, default);
    Require(stat?.ByteLength == bytes.Length && stat.ContentType == "audio/mpeg", "Stat must match uploaded MP3.");
    Require(await store.StatAsync(new(bucket, $"audio/{Guid.NewGuid():N}.mp3"), default) is null, "Missing object must return null.");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var anonymous = await http.GetAsync(publicEndpoint + bucket + "/" + location.Key);
    Require(anonymous.StatusCode == HttpStatusCode.Forbidden, "Unsigned access must be refused.");
    await using var playbackHost = await PlaybackProbeHost.StartAsync(new AudioReadRepository(db), store);
    var link = await playbackHost.GetLinkAsync();
    var publicLink = await playbackHost.GetLinkAsync(publicAudio: true);
    using var publicDownload = await http.GetAsync(publicLink.Url);
    Require(publicDownload.StatusCode == HttpStatusCode.OK && (await publicDownload.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
        "The book-authorized public endpoint must play the real private MP3.");
    using var publicRangeRequest = new HttpRequestMessage(HttpMethod.Get, publicLink.Url);
    publicRangeRequest.Headers.Range = new RangeHeaderValue(10, 41);
    using var publicRange = await http.SendAsync(publicRangeRequest);
    Require(publicRange.StatusCode == HttpStatusCode.PartialContent && (await publicRange.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[10..42]),
        "The public endpoint signed URL must support real MP3 Range.");
    await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=0 WHERE id='probe-book'");
    await playbackHost.RequireDisabledBookAsync();
    Require((await http.GetAsync(publicLink.Url)).StatusCode == HttpStatusCode.OK, "Disabling a book must not revoke an already signed URL.");
    await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=1 WHERE id='probe-book'");
    using var download = await http.GetAsync(link.Url);
    Require(download.StatusCode == HttpStatusCode.OK && download.Content.Headers.ContentType?.MediaType == "audio/mpeg", "Signed GET MIME must match.");
    Require((await download.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Signed download must match bytes.");
    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, link.Url);
    rangeRequest.Headers.Range = new RangeHeaderValue(10, 41);
    using var range = await http.SendAsync(rangeRequest);
    Require(range.StatusCode == HttpStatusCode.PartialContent && (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[10..42]), "Range must serve requested MP3 bytes.");
    // Sign using the same adapter with an elapsed clock: exercise real expiration
    // validation without making every CI run idle for the minimum 60-second TTL.
    using var expiredStore = new S3AudioObjectStore(Options.Create(options), new OffsetClock());
    playbackHost.Store.Inner = expiredStore;
    var expired = await playbackHost.GetLinkAsync();
    var publicExpired = await playbackHost.GetLinkAsync(publicAudio: true);
    Require((await http.GetAsync(publicExpired.Url)).StatusCode == HttpStatusCode.Forbidden, "The public URL expiration must be enforced by real S3.");
    Require((await http.GetAsync(expired.Url)).StatusCode == HttpStatusCode.Forbidden, "Expired signature must be refused.");
    playbackHost.Store.Inner = store;
    var renewed = await playbackHost.GetLinkAsync();
    var publicRenewed = await playbackHost.GetLinkAsync(publicAudio: true);
    Require((await http.GetAsync(publicRenewed.Url)).StatusCode == HttpStatusCode.OK, "The public endpoint must renew an expired URL after authorization.");
    Require((await http.GetAsync(renewed.Url)).StatusCode == HttpStatusCode.OK, "Renewed signature must work.");
    var deniedOptions = new AudioOptions
    {
        Enabled = true,
        InternalEndpoint = endpoint,
        PublicEndpoint = publicEndpoint,
        Bucket = bucket,
        AccessKey = "synthetic-denied-access",
        SecretKey = "synthetic-denied-secret"
    };
    using var denied = new S3AudioObjectStore(Options.Create(deniedOptions), TimeProvider.System);
    playbackHost.Store.Inner = denied;
    await playbackHost.RequireUnavailableAsync();
    await playbackHost.RequireUnavailableAsync(publicAudio: true);
    playbackHost.Store.Inner = store;
    await SafeFailure(() => denied.StatAsync(location, default));
    using var deniedStream = new MemoryStream(bytes);
    await SafeFailure(() => denied.PutAsync(location, deniedStream, bytes.Length, "audio/mpeg", default));
    await SafeFailure(() => denied.RemoveAsync(location, default));
    var deniedUpload = new AudioUploadService(repository, denied, new(true, bucket, "audio/", 3), TimeProvider.System);
    var deniedRequest = request with { IdempotencyKey = Guid.NewGuid().ToString(), Locale = AudioLocale.EnUs,
        SourceRevision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("probe-word", "tone", null, null), null, AudioLocale.EnUs)) };
    Require((await deniedUpload.UploadAsync(deniedRequest, default)).StatusCode == 503,
        "A real S3 denial must produce a safe upload failure.");
    Require((await repository.GetAsync(deniedRequest.IdempotencyKey, default))!.Operation.State == AudioUploadState.Abandoned,
        "A denied upload must retain its known key and failure receipt.");
    Require(await db.VocabularyAudioBindings.CountAsync() == 1,
        "A denied upload must not change the successful binding.");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await store.StatAsync(location, cancelled.Token); throw new InvalidOperationException("Cancellation must be observed."); }
    catch (OperationCanceledException) { }
    // Downloaded real bytes are decoded by Chromium in the following script.
    await File.WriteAllBytesAsync(Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_DOWNLOAD")!, await http.GetByteArrayAsync(publicRenewed.Url));
    await store.RemoveAsync(location, default);
    Require(await store.StatAsync(location, default) is null, "Controlled deletion must remove only the known object.");
    await playbackHost.RequireUnavailableAsync();
    await playbackHost.RequireUnavailableAsync(publicAudio: true);
    // Restore the harness object for the independent backup-retention scenario.
    using (var stream = new MemoryStream(bytes)) await store.PutAsync(location, stream, bytes.Length, "audio/mpeg", default);
    await VerifyRecoveryAsync(db, databaseRoot.Path, repository, store, denied, bucket, bytes, location, http);
    await VerifyProcessCrashesAsync(databaseRoot.Path, store, bucket);
    await store.RemoveAsync(location, default);
    Console.WriteLine("Private S3 upload/stat/GET/MIME/Range/expiry/renewal/denial/deletion passed.");
}
catch (ProbeFailure error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (Exception)
{
    // Do not print SDK responses, exceptions, credentials or signed URLs.
    Console.Error.WriteLine("Audio storage integration failed. Check the isolated test configuration.");
    return 1;
}
return 0;

static void Require(bool condition, string message)
{
    if (!condition) throw new ProbeFailure(message);
}
static async Task SafeFailure(Func<Task> operation)
{
    try { await operation(); throw new InvalidOperationException("Denied credentials must fail."); }
    catch (AudioStorageException error)
    {
        Require(error.InnerException is null && error.Code == "audio.storage.unavailable", "SDK errors must be sanitized.");
    }
}
static async Task VerifyRuntimeRecoveryGenerationsAsync(AudioOptions initial, byte[] bytes)
{
    using var root = new ProbeDatabaseRoot();
    var dbOptions = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={root.Path};Pooling=False").Options;
    var clock = new RecoveryProbeClock { Now = 0 };
    var locations = new List<AudioObjectLocation>();
    await using (var db = new VocabularyDbContext(dbOptions))
    {
        await db.Database.MigrateAsync(); db.Vocabularies.Add(new() { Id = "recovery-word", Word = "tone" });
        db.VocabularyBooks.Add(new() { Id = "recovery-book", BookName = "Recovery" });
        db.VocabularyBookUnits.Add(new() { Id = "recovery-unit", BookId = "recovery-book", Number = 1 });
        db.TextbookAudioTracks.Add(new() { Id = "recovery-track", BookId = "recovery-book", UnitId = "recovery-unit", Title = "Recovery", Version = "11111111-1111-1111-1111-111111111111" });
        await db.SaveChangesAsync();
        var repo = new AudioUploadRepository(db, clock);
        using var store = new S3AudioObjectStore(Options.Create(initial), TimeProvider.System);
        for (var i = 0; i < 3; i++)
        {
            if (i == 1)
            {
                var text = (await new TextbookAudioUploadRepository(db, clock).ReserveAsync(new(Guid.NewGuid().ToString(), new string('b', 64), "recovery-book", "recovery-unit", "recovery-track",
                    "11111111-1111-1111-1111-111111111111", 0.5, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length, initial.Bucket), "crashed-textbook", 0, 300000, default)).Upload!;
                locations.Add(text.Asset.Location);
                using var textContent = new MemoryStream(bytes); await store.PutAsync(text.Asset.Location, textContent, bytes.Length, "audio/mpeg", default);
                continue;
            }
            var candidate = (await repo.ReserveAsync(new(Guid.NewGuid().ToString(), new string('a', 64), "recovery-word", null, AudioLocale.EnGb,
                AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("recovery-word", "tone", null, null), null, AudioLocale.EnGb)),
                null, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length, initial.Bucket), "crashed-upload", 0, 300000, default)).Upload!;
            locations.Add(candidate.Asset.Location);
            using var content = new MemoryStream(bytes); await store.PutAsync(candidate.Asset.Location, content, bytes.Length, "audio/mpeg", default);
        }
    }
    clock.Now = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds();
    var options = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(initial))!; options.Enabled = false; options.RecoveryBatchSize = 2;
    var stores = new RecoveryRotationProbeFactory();
    using var runtime = new AudioRuntimeConfiguration(Options.Create(options), stores);
    var services = new ServiceCollection(); services.AddScoped(_ => new VocabularyDbContext(dbOptions));
    services.AddScoped<IAudioUploadRepository>(sp => new AudioUploadRepository(sp.GetRequiredService<VocabularyDbContext>(), clock));
    services.AddScoped<ITextbookAudioUploadRepository>(sp => new TextbookAudioUploadRepository(sp.GetRequiredService<VocabularyDbContext>(), clock));
    await using var provider = services.BuildServiceProvider();
    using var worker = new AudioRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), runtime, clock,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioRecoveryWorker>.Instance);
    await worker.StartAsync(default);
    Require(!worker.ExecuteTask!.IsCompleted && stores.Stores[0].Calls == 0, "Disabled recovery must wait without storage IO.");
    options.Enabled = true; await Publish(options);
    var previous = stores.Stores[1]; await previous.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    options.Enabled = false; options.AccessKey = "synthetic-setup-access"; options.SecretKey = "synthetic-setup-secret";
    options.PublicEndpoint = initial.PublicEndpoint.Replace("/s3/", "/"); await Publish(options);
    Require(!previous.Disposed, "Paused real recovery must keep its old client during disable/rotation.");
    previous.Finish.TrySetResult(); await previous.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Require(previous.Calls == 4 && stores.Stores[2].Calls == 0, "A bounded two-object batch must keep one old client; disabled generation performs no IO.");
    options.Enabled = true; await Publish(options);
    await stores.Stores[3].Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        await using var db = new VocabularyDbContext(dbOptions);
        if (await db.AudioUploadOperations.CountAsync(o => o.State == AudioUploadState.CleanupObserved)
            + await db.TextbookAudioUploadOperations.CountAsync(o => o.State == AudioUploadState.CleanupObserved) == 3) break;
        Require(deadline.Elapsed < TimeSpan.FromSeconds(10), "Re-enabled recovery must observe the remaining real object.");
        await Task.Delay(20);
    }
    await worker.StopAsync(default);
    using var verify = new S3AudioObjectStore(Options.Create(initial), TimeProvider.System);
    foreach (var location in locations) Require(await verify.StatAsync(location, default) is null, "Runtime recovery must delete every known unpublished object.");
    Require(stores.Stores[3].Calls == 2, "Re-enable must use the rotated real client for the remaining bounded batch.");
    Console.WriteLine("Private S3 mixed vocabulary/textbook disabled-start recovery, shared batch bound, old-generation cleanup, rotation and re-enable passed.");
    async Task Publish(AudioOptions candidate)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = candidate }))).Build();
        using var prepared = await runtime.PrepareAsync(configuration, default);
        Require(prepared is not null, "Internal recovery candidate must be valid."); prepared!.Publish();
    }
}

static async Task VerifyRuntimeUploadGenerationsAsync(AudioOptions initial, byte[] bytes)
{
    using var root = new ProbeDatabaseRoot();
    var dbOptions = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={root.Path};Pooling=False").Options;
    await using var oldDb = new VocabularyDbContext(dbOptions);
    await oldDb.Database.MigrateAsync();
    oldDb.Vocabularies.Add(new() { Id = "runtime-word", Word = "tone" });
    await oldDb.SaveChangesAsync();
    var factory = new RuntimeProbeStoreFactory();
    using var runtime = new AudioRuntimeConfiguration(Options.Create(initial), factory, new RuntimeProbeRecords(dbOptions));
    using var old = await new AudioUploadOperationFactory(runtime, new AudioUploadRepository(oldDb), TimeProvider.System).AcquireAsync(default);
    var revision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("runtime-word", "tone", null, null), null, AudioLocale.EnGb));
    var upload = old.Service.UploadAsync(new(Guid.NewGuid().ToString(), "runtime-word", null, AudioLocale.EnGb, revision, null, bytes, null, null), default);
    await factory.Stores[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var rotated = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(initial))!;
    rotated.AccessKey = "synthetic-setup-access";
    rotated.SecretKey = "synthetic-setup-secret";
    using (var candidate = await runtime.PrepareAsync(RuntimeConfig(rotated), default))
    {
        Require(candidate is not null, "Same-destination credential rotation must prepare locally.");
        candidate!.Publish();
    }
    await using var newDb = new VocabularyDbContext(dbOptions);
    using var current = await new AudioUploadOperationFactory(runtime, new AudioUploadRepository(newDb), TimeProvider.System).AcquireAsync(default);
    var us = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("runtime-word", "tone", null, null), null, AudioLocale.EnUs));
    var newer = await current.Service.UploadAsync(new(Guid.NewGuid().ToString(), "runtime-word", null, AudioLocale.EnUs, us, null, bytes, null, null), default);
    Require(newer.StatusCode == 200, "New generation must upload while the old Put is paused.");
    Require(!factory.Stores[0].Disposed && factory.Stores[0].Options.AccessKey == "synthetic-audio-access"
        && factory.Stores[1].Options.AccessKey == "synthetic-setup-access", "Concurrent real S3 uploads must keep their own clients.");
    var destination = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(rotated))!;
    destination.KeyPrefix = "new/";
    try
    {
        using var rejected = await runtime.PrepareAsync(RuntimeConfig(destination), default);
        throw new InvalidOperationException("Active audio operations must reject destination changes.");
    }
    catch (ConfigurationChangeConflictException) { }
    factory.Stores[0].Finish.TrySetResult();
    Require((await upload).StatusCode == 200, "The old generation must finish using its original real S3 client.");
    var locations = await oldDb.AudioAssets.AsNoTracking().Select(asset => new AudioObjectLocation(asset.Bucket, asset.ObjectKey)).ToListAsync();
    using var verifier = new S3AudioObjectStore(Options.Create(initial), TimeProvider.System);
    foreach (var location in locations)
    {
        Require(location.Bucket == initial.Bucket && location.Key.StartsWith(initial.KeyPrefix, StringComparison.Ordinal)
            && (await verifier.StatAsync(location, default))?.ByteLength == bytes.Length, "Each database location must identify the object actually uploaded.");
        var signed = await verifier.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(60), default);
        using var http = new HttpClient();
        Require((await http.GetByteArrayAsync(signed.Url)).SequenceEqual(bytes), "Both generations must store the complete real MP3.");
    }
    old.Dispose(); current.Dispose();
    Require(factory.Stores[0].Disposed, "Retired real S3 client must be disposed after the old operation ends.");
    try
    {
        using var rejected = await runtime.PrepareAsync(RuntimeConfig(destination), default);
        throw new InvalidOperationException("Historical audio records must reject destination changes.");
    }
    catch (ConfigurationChangeConflictException) { }
    oldDb.VocabularyBooks.Add(new() { Id = "runtime-book", BookName = "Runtime", Status = true });
    oldDb.VocabularyMeanings.Add(new() { Id = "runtime-meaning", VocabularyId = "runtime-word", BookId = "runtime-book", Meaning = "tone" });
    await oldDb.SaveChangesAsync();
    foreach (var publicAudio in new[] { false, true })
    {
        var playbackFactory = new PlaybackRotationProbeFactory(publicAudio ? 2 : 1);
        using var playbackRuntime = new AudioRuntimeConfiguration(Options.Create(initial), playbackFactory);
        using var previous = await new AudioPlaybackOperationFactory(playbackRuntime, new AudioReadRepository(oldDb),
            new AudioReadRepository(oldDb)).AcquireAsync(default);
        var pending = publicAudio ? previous.Public.GetAsync("runtime-book", "runtime-word", null, null, default)
            : AdminResults(previous.Admin);
        await playbackFactory.Stores[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newPlaybackOptions = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(rotated))!;
        newPlaybackOptions.PublicEndpoint = initial.InternalEndpoint;
        using (var candidate = await playbackRuntime.PrepareAsync(RuntimeConfig(newPlaybackOptions), default))
        {
            Require(candidate is not null, "Real playback credential/PublicEndpoint rotation must prepare.");
            candidate!.Publish();
        }
        using var next = await new AudioPlaybackOperationFactory(playbackRuntime, new AudioReadRepository(newDb),
            new AudioReadRepository(newDb)).AcquireAsync(default);
        var currentResults = publicAudio ? await next.Public.GetAsync("runtime-book", "runtime-word", null, null, default)
            : await AdminResults(next.Admin);
        Require(!playbackFactory.Stores[0].Disposed, "Paused old real signatures must retain their client.");
        playbackFactory.Stores[0].Finish.TrySetResult();
        var previousResults = await pending;
        using var http = new HttpClient();
        foreach (var result in previousResults)
        {
            Require(result.Link!.Url.StartsWith(initial.PublicEndpoint, StringComparison.Ordinal), "All old accents must retain the proxy prefix.");
            Require((await http.GetByteArrayAsync(result.Link.Url)).SequenceEqual(bytes), "Each old real signature must download the complete MP3.");
        }
        foreach (var result in currentResults)
        {
            Require(result.Link!.Url.StartsWith(newPlaybackOptions.PublicEndpoint, StringComparison.Ordinal), "New playback must use the new public endpoint.");
            Require((await http.GetByteArrayAsync(result.Link.Url)).SequenceEqual(bytes), "Each new credential signature must download the complete MP3.");
        }
        previous.Dispose();
        Require(playbackFactory.Stores[0].Disposed, "Old signing client disposes only after the playback operation ends.");
    }
    Console.WriteLine("Concurrent runtime playback generations passed.");
    foreach (var location in locations) await verifier.RemoveAsync(location, default);
    Console.WriteLine("Concurrent runtime upload generations passed.");
}
static async Task<IReadOnlyList<AudioPlaybackResult>> AdminResults(AudioPlaybackService playback)
    => new[] { await playback.GetAsync("runtime-word", null, AudioLocale.EnGb, default) };
static IConfiguration RuntimeConfig(AudioOptions options) => new ConfigurationBuilder()
    .AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }))).Build();

static async Task VerifyRecoveryAsync(VocabularyDbContext db, string databasePath, AudioUploadRepository liveRepository,
    IAudioObjectStore store, IAudioObjectStore denied, string bucket, byte[] bytes,
    AudioObjectLocation published, HttpClient http)
{
    var backupPath = databasePath + ".backup";
    await using (var source = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
    await using (var destination = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
    {
        await source.OpenAsync(); await destination.OpenAsync(); source.BackupDatabase(destination);
    }
    var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var request = new AudioUploadRequest(Guid.NewGuid().ToString(), new string('a', 64), "probe-word", null, AudioLocale.EnUs,
        AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("probe-word", "tone", null, null), null, AudioLocale.EnUs)),
        null, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length, bucket);
    var candidate = (await liveRepository.ReserveAsync(request, "crashed-upload", created, created + 300000, default)).Upload!;
    await liveRepository.BeginTransferAsync(request.IdempotencyKey, candidate.Operation.UploadLease, created, default);
    using (var stream = new MemoryStream(bytes)) await store.PutAsync(candidate.Asset.Location, stream, bytes.Length, "audio/mpeg", default);
    var clock = new RecoveryProbeClock { Now = created + 86400001 };
    // A fresh context/coordinator models restart: no upload process memory is reused.
    await using var restarted = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={databasePath};Pooling=False").Options);
    var repository = new AudioUploadRepository(restarted, clock);
    var settings = new AudioRecoverySettings(true, bucket, "audio/", TimeoutSeconds: 3);
    var deniedRecovery = new AudioRecoveryService(repository, denied, settings, clock);
    Require((await deniedRecovery.RunBatchAsync(default)).Failed > 0, "Denied recovery must persist a retry.");
    var failed = (await repository.GetAsync(request.IdempotencyKey, default))!;
    Require(failed.Operation.Cleanup.LastErrorCode == "audio.cleanup_failed" && failed.Operation.Receipt?.StatusCode == 503,
        "Recovery failure must retain a safe immutable receipt.");
    var recovery = new AudioRecoveryService(repository, store, settings, clock);
    Require((await recovery.RunBatchAsync(default)).Examined == 0, "Recovery must honor retry time.");
    clock.Now = failed.Operation.Cleanup.NextAttemptAtUnixMs!.Value;
    Require((await recovery.RunBatchAsync(default)).Observed > 0 && await store.StatAsync(candidate.Asset.Location, default) == null,
        "Recovery must remove the real unpublished MP3.");
    var observed = (await repository.GetAsync(request.IdempotencyKey, default))!;
    using (var stream = new MemoryStream(bytes)) await store.PutAsync(candidate.Asset.Location, stream, bytes.Length, "audio/mpeg", default);
    Require((await recovery.RunBatchAsync(default)).Examined == 0, "A late remote Put must wait for the retained recheck.");
    clock.Now = observed.Operation.Cleanup.NextAttemptAtUnixMs!.Value;
    Require((await recovery.RunBatchAsync(default)).Observed > 0 && await store.StatAsync(candidate.Asset.Location, default) == null,
        "Retained tombstones must delete a late real Put at the same key.");
    Require(await repository.CompleteAsync(request.IdempotencyKey, candidate.Operation.UploadLease, clock.Now, default) == null,
        "Expired uploads must never republish after cleanup.");
    // Logical deletion and the legacy direct SQL cleanup do not erase published objects.
    var own = await db.VocabularyAudioBindings.AsNoTracking().SingleAsync();
    await liveRepository.UnbindAsync("probe-word", null, AudioLocale.EnGb, own.AssetId, default);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='probe-word'");
    await recovery.RunBatchAsync(default);
    Require(await store.StatAsync(published, default) != null, "Published unreferenced objects must survive recovery.");
    await using var backup = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={backupPath};Pooling=False").Options);
    var snapshot = await new AudioReadRepository(backup).GetSnapshotAsync("probe-word", null, default);
    Require(snapshot?.Bindings.Single().Asset.Location == published, "The historical SQLite backup must still reference its asset.");
    var link = await store.GetPlaybackLinkAsync(snapshot!.Bindings.Single().Asset.Location, TimeSpan.FromSeconds(60), default);
    Require((await http.GetByteArrayAsync(link.Url)).SequenceEqual(bytes), "Restoring the backup must still download its real published MP3.");
    Console.WriteLine("Private S3 restart/retry/late-Put/tombstone/backup-retention recovery passed.");
}
static async Task CrashUploadAsync(string databasePath, string markerPath, string stage)
{
    await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={databasePath};Pooling=False").Options);
    var word = "crash-" + stage;
    db.Vocabularies.Add(new VocabularyEntity { Id = word, Word = word }); await db.SaveChangesAsync();
    var bucket = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_BUCKET")!;
    using var store = new S3AudioObjectStore(Options.Create(new AudioOptions
    {
        Enabled = true, Bucket = bucket, InternalEndpoint = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_INTERNAL")!,
        PublicEndpoint = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_PUBLIC")!, AccessKey = "synthetic-audio-access",
        SecretKey = "synthetic-audio-secret", OperationTimeoutSeconds = 3
    }), TimeProvider.System);
    var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    var repo = new AudioUploadRepository(db);
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var request = new AudioUploadRequest(Guid.NewGuid().ToString(), new string('b', 64), word, null, AudioLocale.EnGb,
        AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new(word, word, null, null), null, AudioLocale.EnGb)),
        null, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length, bucket);
    var upload = (await repo.ReserveAsync(request, "crash-process", now, now + 300000, default)).Upload!;
    if (stage != "reserved") await repo.BeginTransferAsync(request.IdempotencyKey, upload.Operation.UploadLease, now, default);
    if (stage is "after-put" or "response-lost")
    {
        using var content = new MemoryStream(bytes);
        await store.PutAsync(upload.Asset.Location, content, bytes.Length, "audio/mpeg", default);
    }
    if (stage == "response-lost") await repo.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, now, default);
    await File.WriteAllTextAsync(markerPath, request.IdempotencyKey);
    await Task.Delay(Timeout.InfiniteTimeSpan); // The parent kills this process without disposing SQLite/S3.
}
static async Task VerifyProcessCrashesAsync(string databasePath, IAudioObjectStore store, string bucket)
{
    var keys = new List<(string Key, bool Published)>();
    foreach (var stage in new[] { "reserved", "before-put", "after-put", "response-lost" })
    {
        var marker = databasePath + "." + stage;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(ProbeFailure).Assembly.Location, "--crash-upload", databasePath, marker, stage }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!File.Exists(marker) && !process.HasExited && DateTimeOffset.UtcNow < until) await Task.Delay(50);
            Require(File.Exists(marker) && !process.HasExited, "The isolated crash process must persist its stage before termination.");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            keys.Add((await File.ReadAllTextAsync(marker), stage == "response-lost"));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    var clock = new RecoveryProbeClock { Now = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds() };
    await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={databasePath};Pooling=False").Options);
    var repo = new AudioUploadRepository(db, clock);
    await new AudioRecoveryService(repo, store, new(true, bucket, "audio/", TimeoutSeconds: 3), clock).RunBatchAsync(default);
    foreach (var (key, published) in keys)
    {
        var persisted = (await repo.GetAsync(key, default))!;
        Require(persisted.Operation.State == (published ? AudioUploadState.Succeeded : AudioUploadState.CleanupObserved),
            "Recovery after actual process termination must preserve success and clean only unpublished candidates.");
        Require((await store.StatAsync(persisted.Asset.Location, default) != null) == published,
            "Actual process termination must not lose published S3 data.");
    }
    Console.WriteLine("Private S3 recovery after killed processes at reservation/before-Put/after-Put/lost-response passed.");
}
sealed class RecoveryProbeClock : TimeProvider
{
    public long Now;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
}

sealed class OffsetClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMinutes(-2);
}

sealed class ProbeFailure(string message) : Exception(message);

sealed class ProbeDatabaseRoot : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lexarbor-audio-upload-probe-" + Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(_root, "probe.db");
    public ProbeDatabaseRoot() => Directory.CreateDirectory(_root);
    public void Dispose() { Directory.Delete(_root, recursive: true); }
}

// Real HTTP mapper and private S3. Full production identity/role cases live in
// AudioAdminPlaybackEndpointTests; this isolated host supplies only a test identity.
sealed class PlaybackProbeHost(WebApplication app, HttpClient client, PlaybackProbeStore store) : IAsyncDisposable
{
    public PlaybackProbeStore Store => store;
    public static async Task<PlaybackProbeHost> StartAsync(AudioReadRepository repository, IAudioObjectStore objectStore)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAuthorization(options => options.AddPolicy(AdminEndpointAuthorization.PolicyName, policy => policy.RequireRole("admin")));
        builder.Services.AddSingleton<IAudioReadRepository>(repository);
        builder.Services.AddSingleton<IAudioPublicReadRepository>(repository);
        var store = new PlaybackProbeStore(objectStore);
        builder.Services.AddSingleton<IAudioObjectStore>(store);
        builder.Services.AddSingleton(new AudioRuntimeConfiguration(Options.Create(new AudioOptions
        {
            Enabled = true, InternalEndpoint = "http://127.0.0.1:9/", PublicEndpoint = "https://audio.example.test/",
            AccessKey = "synthetic-probe-access", SecretKey = "synthetic-probe-secret", PlaybackTtlSeconds = 60,
            OperationTimeoutSeconds = 3
        }), new PlaybackProbeFactory(store)));
        builder.Services.AddScoped<IAudioPlaybackOperationFactory, AudioPlaybackOperationFactory>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin"))
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "isolated-probe"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapAudioAdminPlaybackEndpoints();
        app.MapAudioPublicPlaybackEndpoints();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Lexarbor.Domain.Exceptions.BusinessRuleException)
            {
                context.Response.StatusCode = 422;
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) }, store);
    }
    public async Task<AudioPlaybackLink> GetLinkAsync(bool publicAudio = false)
    {
        using var response = await client.GetAsync(publicAudio ? "/api/vocabulary/probe-word/audio?bookId=probe-book&locale=en-GB"
            : "/admin/vocabulary/probe-word/audio/en-GB/playback");
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.ToString() != "no-store")
            throw new ProbeFailure("The real administrator playback endpoint must return no-store success.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        if (publicAudio) data = data.GetProperty("items")[0];
        if (data.GetProperty("status").GetString() != "ready" || data.GetProperty("source").GetString() != "default")
            throw new ProbeFailure("Playback must use the published default binding.");
        var expires = data.GetProperty("expiresAt").GetDateTimeOffset();
        if (expires.Offset != TimeSpan.Zero) throw new ProbeFailure("Playback expiry must be UTC.");
        return new(data.GetProperty("url").GetString()!, expires);
    }
    public async Task RequireUnavailableAsync(bool publicAudio = false)
    {
        using var response = await client.GetAsync(publicAudio ? "/api/vocabulary/probe-word/audio?bookId=probe-book&locale=en-GB"
            : "/admin/vocabulary/probe-word/audio/en-GB/playback");
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable || response.Headers.CacheControl?.ToString() != "no-store"
            || await response.Content.ReadAsStringAsync() != "{\"success\":false,\"message\":\"Audio storage is unavailable.\"}")
            throw new ProbeFailure("Real S3 denial or lost object must yield the fixed no-store playback failure.");
    }
    public async Task RequireDisabledBookAsync()
    {
        using var response = await client.GetAsync("/api/vocabulary/probe-word/audio?bookId=probe-book&locale=en-GB");
        if (response.StatusCode != HttpStatusCode.UnprocessableEntity) throw new ProbeFailure("A disabled book cannot receive a new public URL.");
    }
    public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
}
sealed class PlaybackProbeStore(IAudioObjectStore inner) : IAudioObjectStore
{
    public IAudioObjectStore Inner { get; set; } = inner;
    public Task PutAsync(AudioObjectLocation location, Stream content, long size, string type, CancellationToken token)
        => Inner.PutAsync(location, content, size, type, token);
    public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => Inner.StatAsync(location, token);
    public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => Inner.RemoveAsync(location, token);
    public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan ttl, CancellationToken token)
        => Inner.GetPlaybackLinkAsync(location, ttl, token);
}

sealed class RuntimeProbeRecords(DbContextOptions<VocabularyDbContext> options) : IAudioDestinationRecords
{
    public async Task<bool> HasAnyAsync(CancellationToken cancellationToken)
    {
        await using var db = new VocabularyDbContext(options);
        return await new AudioRecordExistenceRepository(db).HasAnyAsync(cancellationToken);
    }
}
sealed class RuntimeProbeStoreFactory : IAudioObjectStoreFactory
{
    public List<RuntimeProbeStore> Stores { get; } = [];
    public IAudioObjectStore Create(AudioOptions options)
    {
        var store = new RuntimeProbeStore(options, Stores.Count == 0);
        Stores.Add(store);
        return store;
    }
}
sealed class RuntimeProbeStore(AudioOptions options, bool pause) : IAudioObjectStore, IDisposable
{
    private readonly S3AudioObjectStore _inner = new(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
    public AudioOptions Options { get; } = options;
    public bool Disposed { get; private set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        if (pause) await Finish.Task.WaitAsync(cancellationToken);
        if (Disposed || location.Bucket != Options.Bucket || !location.Key.StartsWith(Options.KeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Upload generation changed during transfer.");
        await _inner.PutAsync(location, content, byteLength, contentType, cancellationToken);
    }
    public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => _inner.StatAsync(location, token);
    public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => _inner.RemoveAsync(location, token);
    public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => _inner.GetPlaybackLinkAsync(location, validity, token);
    public void Dispose() { Disposed = true; _inner.Dispose(); }
}

sealed class PlaybackProbeFactory(IAudioObjectStore store) : IAudioObjectStoreFactory
{
    public IAudioObjectStore Create(AudioOptions options) => store;
}

sealed class PlaybackRotationProbeFactory(int pauseAt) : IAudioObjectStoreFactory
{
    public List<PlaybackRotationProbeStore> Stores { get; } = [];
    public IAudioObjectStore Create(AudioOptions options)
    {
        var store = new PlaybackRotationProbeStore(options, Stores.Count == 0 ? pauseAt : 0);
        Stores.Add(store); return store;
    }
}
sealed class PlaybackRotationProbeStore(AudioOptions options, int pauseAt) : IAudioObjectStore, IDisposable
{
    private readonly S3AudioObjectStore _inner = new(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
    private int _signs;
    public bool Disposed { get; private set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token)
    {
        if (Interlocked.Increment(ref _signs) == pauseAt)
        { Started.TrySetResult(); await Finish.Task.WaitAsync(token); }
        if (Disposed) throw new InvalidOperationException("Playback client was disposed before signing ended.");
        return await _inner.GetPlaybackLinkAsync(location, validity, token);
    }
    public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => _inner.StatAsync(location, token);
    public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token) => throw new NotSupportedException();
    public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new NotSupportedException();
    public void Dispose() { Disposed = true; _inner.Dispose(); }
}

sealed class RecoveryRotationProbeFactory : IAudioObjectStoreFactory
{
    public readonly List<RecoveryRotationProbeStore> Stores = [];
    public IAudioObjectStore Create(AudioOptions options)
    { var store = new RecoveryRotationProbeStore(options, Stores.Count == 1); Stores.Add(store); return store; }
}
sealed class RecoveryRotationProbeStore(AudioOptions options, bool pause) : IAudioObjectStore, IDisposable
{
    private readonly S3AudioObjectStore _inner = new(Options.Create(options), TimeProvider.System);
    public int Calls; public bool Disposed;
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Finish = new(TaskCreationOptions.RunContinuationsAsynchronously), DisposedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task RemoveAsync(AudioObjectLocation location, CancellationToken token)
    {
        if (Interlocked.Increment(ref Calls) == 1) { Entered.TrySetResult(); if (pause) await Finish.Task.WaitAsync(token); }
        RequireClient(); await _inner.RemoveAsync(location, token);
    }
    public async Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token)
    { Interlocked.Increment(ref Calls); RequireClient(); return await _inner.StatAsync(location, token); }
    private void RequireClient() { if (Disposed) throw new InvalidOperationException("Recovery client was released before completion."); }
    public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token) => throw new NotSupportedException();
    public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => throw new NotSupportedException();
    public void Dispose() { Disposed = true; _inner.Dispose(); DisposedSignal.TrySetResult(); }
}
