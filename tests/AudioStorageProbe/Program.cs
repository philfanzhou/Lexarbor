using System.Net;
using System.Net.Http.Headers;
using Lexarbor.Domain.Models;
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
    var location = new AudioObjectLocation(bucket, $"audio/{Guid.NewGuid():N}.mp3");
    var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
    using var stream = new MemoryStream(bytes);
    await store.PutAsync(location, stream, bytes.Length, "audio/mpeg", default);
    Require(stream.CanRead, "Caller stream must remain open.");
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
