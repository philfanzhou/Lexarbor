using System.Net;
using System.Net.Http.Headers;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Service;
using Microsoft.EntityFrameworkCore;
using Lexarbor.Host.Audio;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

try
{
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
    using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
    Require(Mp3Validation.IsValid(bytes), "The uploaded fixture must contain complete MP3 frames.");
    using var databaseRoot = new ProbeDatabaseRoot();
    await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={databaseRoot.Path};Pooling=False").Options);
    await db.Database.MigrateAsync();
    db.Vocabularies.Add(new VocabularyEntity { Id = "probe-word", Word = "tone" });
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
    var link = await store.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(60), default);
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
    var expired = await expiredStore.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(60), default);
    Require((await http.GetAsync(expired.Url)).StatusCode == HttpStatusCode.Forbidden, "Expired signature must be refused.");
    var renewed = await store.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(60), default);
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
    await File.WriteAllBytesAsync(Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_DOWNLOAD")!, await http.GetByteArrayAsync(renewed.Url));
    await store.RemoveAsync(location, default);
    Require(await store.StatAsync(location, default) is null, "Controlled deletion must remove only the known object.");
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
