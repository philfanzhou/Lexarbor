using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceMantle.Web.Management;

// Production settings/upload/query/playback/recovery paths with disposable real S3.
// Authentication/CSRF/security-header cases use the complete Host in service tests.
internal static class SettingsApiProbe
{
    public static async Task VerifyAsync(AudioOptions options, byte[] mp3)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "settings-api-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var stage = "database initialization";
        try
        {
            var dbOptions = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={root}/probe.db;Pooling=False").Options;
            await using (var db = new VocabularyDbContext(dbOptions))
            {
                await db.Database.MigrateAsync();
                db.Vocabularies.Add(new() { Id = "settings-word", Word = "tone" });
                db.VocabularyBooks.Add(new() { Id = "settings-book", BookName = "Settings", Status = true });
                db.VocabularyMeanings.Add(new() { Id = "settings-meaning", VocabularyId = "settings-word", BookId = "settings-book", Meaning = "tone" });
                await db.SaveChangesAsync();
            }
            string assetId;
            string sourceRevision;
            AudioObjectLocation recoveryObject;
            stage = "Host startup";
            await using (var host = await Host.StartAsync(root, dbOptions))
            {
                var initial = await host.ReadAsync(); Check(!initial.Enabled && !File.Exists(Path.Combine(root, "data/appsettings.json")), "GET must not create configuration.");
                stage = "initial settings save";
                await host.SaveAsync(new
                {
                    enabled = true,
                    internalEndpoint = options.InternalEndpoint,
                    publicEndpoint = options.PublicEndpoint,
                    options.Region,
                    options.Bucket,
                    keyPrefix = "settings/",
                    accessKey = new { action = "replace", value = options.AccessKey },
                    secretKey = new { action = "replace", value = options.SecretKey }
                });
                stage = "MP3 upload";
                using var uploadBody = new MultipartFormDataContent();
                sourceRevision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("settings-word", "tone", null, null), null, AudioLocale.EnGb));
                uploadBody.Add(new StringContent(sourceRevision), "sourceRevision"); uploadBody.Add(new StringContent(""), "expectedAssetId");
                uploadBody.Add(new ByteArrayContent(mp3), "file", "tone.mp3");
                using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/vocabulary/settings-word/audio/en-GB") { Content = uploadBody };
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                using var upload = await host.Client.SendAsync(request);
                Check(upload.StatusCode == HttpStatusCode.OK, "API-configured upload must publish a private MP3.");
                using var payload = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
                assetId = payload.RootElement.GetProperty("data").GetProperty("assetId").GetString()!;
                stage = "administrator/public playback";
                await host.PlayAsync(mp3);
                stage = "destination guard";
                var before = await host.ReadAsync();
                using var conflict = await host.SendSaveAsync(before.Revision, new { bucket = "other-bucket" });
                Check(conflict.StatusCode == HttpStatusCode.Conflict && (await host.ReadAsync()).Revision == before.Revision, "Stored audio must reject destination changes with 409.");
                stage = "disable/metadata";
                await host.SaveAsync(new { enabled = false });
                using var disabled = await host.Client.GetAsync("/admin/vocabulary/settings-word/audio/en-GB/playback");
                Check(disabled.StatusCode == HttpStatusCode.ServiceUnavailable, "Disabled API configuration blocks new playback.");
                using var metadata = await host.Client.GetAsync("/admin/vocabulary/settings-word/audio");
                using var metadataJson = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
                Check(!metadataJson.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean(), "Metadata observes saved disable.");
                // Seed a real, expired, never-published candidate while worker is disabled.
                // The subsequent API enable must wake the initially-disabled worker.
                stage = "unpublished recovery seed";
                var unpublishedId = Guid.NewGuid().ToString("N"); recoveryObject = new(options.Bucket, "settings/" + unpublishedId + ".mp3");
                var recoveryOptions = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(options))!; recoveryOptions.KeyPrefix = "settings/";
                using var store = new S3AudioObjectStore(Options.Create(recoveryOptions), TimeProvider.System);
                await store.PutAsync(recoveryObject, new MemoryStream(mp3), mp3.Length, "audio/mpeg", default);
                await using var db = new VocabularyDbContext(dbOptions);
                db.AudioAssets.Add(new()
                {
                    Id = unpublishedId,
                    Bucket = recoveryObject.Bucket,
                    ObjectKey = recoveryObject.Key,
                    Sha256 = new string('b', 64),
                    ByteLength = mp3.Length,
                    CreatedAtUnixMs = 1
                });
                db.AudioUploadOperations.Add(new()
                {
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    AssetId = unpublishedId,
                    WordId = "settings-word",
                    Locale = AudioLocale.EnGb,
                    SourceRevision = sourceRevision,
                    RequestFingerprint = new string('b', 64),
                    State = AudioUploadState.Abandoned,
                    CreatedAtUnixMs = 1,
                    UpdatedAtUnixMs = 1,
                    CompletedAtUnixMs = 1,
                    CleanupNextAttemptAtUnixMs = 1,
                    ReceiptStatusCode = 503,
                    ReceiptErrorCode = "storage-unavailable"
                });
                await db.SaveChangesAsync();
                stage = "re-enable/recovery";
                await host.SaveAsync(new { enabled = true, accessKey = new { action = "keep" }, secretKey = new { action = "keep" } });
                for (var attempt = 0; attempt < 100 && await store.StatAsync(recoveryObject, default) is not null; attempt++) await Task.Delay(100);
                Check(await store.StatAsync(recoveryObject, default) is null, "API re-enable must recover the private unpublished object.");
                await host.PlayAsync(mp3);
            }
            stage = "restart/playback";
            await using (var restarted = await Host.StartAsync(root, dbOptions))
            {
                var saved = await restarted.ReadAsync(); Check(saved.Enabled && saved.HasAccessKey && saved.HasSecretKey && saved.KeyPrefix == "settings/", "Restart must read saved complete S3 settings.");
                await restarted.PlayAsync(mp3);
                await using var db = new VocabularyDbContext(dbOptions);
                Check(await db.VocabularyAudioBindings.AnyAsync(binding => binding.AssetId == assetId), "Restart keeps the original binding.");
                Check(await db.AudioAssets.AnyAsync(asset => asset.Id == assetId && asset.FirstPublishedAtUnixMs != null), "Recovery keeps the published object.");
            }
        }
        catch (Exception) { throw new ProbeFailure("Settings API integration failed at " + stage + "."); }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new ProbeFailure(message); }

    private sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        private const string Url = "/admin/system/settings/audio-storage";
        public HttpClient Client => client;
        public static async Task<Host> StartAsync(string root, DbContextOptions<VocabularyDbContext> dbOptions)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Audio:OperationTimeoutSeconds"] = "3", ["Audio:RecoveryIntervalSeconds"] = "10" });
            var file = new PersistentConfigurationFileStore(root);
            builder.Configuration.AddJsonFile(file.FilePath, optional: true, reloadOnChange: false);
            builder.Services.AddSingleton(file);
            builder.Services.AddSingleton(provider => new PersistentConfigurationCoordinator((IConfigurationRoot)provider.GetRequiredService<IConfiguration>(), file));
            builder.Services.AddSingleton(new RateLimitRuntime(Options.Create(new RateLimitOptions())));
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddAudioStorage(builder.Configuration);
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(ManagementAuthorizationDefaults.AdminPolicyName, policy => policy.RequireRole("admin"));

            });
            builder.Services.AddScoped(_ => new VocabularyDbContext(dbOptions));
            builder.Services.AddScoped<IAudioRecordExistenceRepository, AudioRecordExistenceRepository>();
            builder.Services.AddScoped<IAudioUploadRepository, AudioUploadRepository>();
            builder.Services.AddScoped<IAudioReadRepository, AudioReadRepository>();
            builder.Services.AddScoped<IAudioPublicReadRepository, AudioReadRepository>();
            builder.Services.AddScoped<AudioAdminQueryService>();
            builder.Services.AddScoped<IAudioUploadOperationFactory, AudioUploadOperationFactory>();
            builder.Services.AddScoped<IAudioAdminOperationFactory, AudioAdminOperationFactory>();
            builder.Services.AddScoped<IAudioPlaybackOperationFactory, AudioPlaybackOperationFactory>();
            builder.Services.AddSingleton(new AudioUploadAdmission(2, 8, TimeSpan.FromSeconds(3)));
            builder.Services.AddHostedService<AudioRecoveryWorker>();
            var app = builder.Build();
            app.Services.GetRequiredService<PersistentConfigurationCoordinator>();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/admin")) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "isolated-probe"));
                await next(context);
            });
            app.UseSystemSettingsNoStore(); app.UseAuthorization(); app.MapSystemSettingsEndpoints();
            app.MapAudioUploadEndpoints(); app.MapAudioAdminQueryEndpoints(); app.MapAudioAdminPlaybackEndpoints(); app.MapAudioPublicPlaybackEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) });
        }
        public async Task<AudioStorageSettingsEndpoints.AudioStorageResponse> ReadAsync()
        {
            using var response = await client.GetAsync(Url);
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.ToString() == "no-store", "Settings GET must succeed without caching.");
            var body = await response.Content.ReadAsStringAsync(); Check(!body.Contains("synthetic-audio-", StringComparison.Ordinal), "Settings responses must not contain credentials.");
            return JsonSerializer.Deserialize<AudioStorageSettingsEndpoints.AudioStorageResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        public Task<HttpResponseMessage> SendSaveAsync(string revision, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, Url) { Content = JsonContent.Create(body) }; request.Headers.TryAddWithoutValidation("If-Match", revision);
            return client.SendAsync(request);
        }
        public async Task SaveAsync(object body)
        {
            var state = await ReadAsync(); using var response = await SendSaveAsync(state.Revision, body);
            Check(response.StatusCode == HttpStatusCode.OK, "Valid settings API save must succeed.");
            Check(!(await response.Content.ReadAsStringAsync()).Contains("synthetic-audio-", StringComparison.Ordinal), "Settings PUT must not return credentials.");
        }
        public async Task PlayAsync(byte[] mp3)
        {
            using var download = new HttpClient();
            foreach (var route in new[] { "/admin/vocabulary/settings-word/audio/en-GB/playback", "/api/vocabulary/settings-word/audio?bookId=settings-book&locale=en-GB" })
            {
                using var response = await client.GetAsync(route); Check(response.StatusCode == HttpStatusCode.OK, "Saved configuration must support administrator and public playback.");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement.GetProperty("data");
                if (route.StartsWith("/api", StringComparison.Ordinal)) data = data.GetProperty("items")[0];
                var url = data.GetProperty("url").GetString()!; using var audio = await download.GetAsync(url);
                Check(audio.StatusCode == HttpStatusCode.OK && (await audio.Content.ReadAsByteArrayAsync()).SequenceEqual(mp3), "Private MP3 signed playback must return the uploaded bytes.");
            }
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
