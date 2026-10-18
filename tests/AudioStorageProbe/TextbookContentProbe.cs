using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Audio;
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

// Production content HTTP/parser/decoder/coordinator/repository/S3 path. Full Host tests own auth/CSRF assertions.
internal static class TextbookContentProbe
{
    private const string Root = "/admin/vocabulary-books/content-book/units/content-unit/audio-tracks";
    public static async Task VerifyAsync(AudioOptions options, byte[] mp3)
    {
        using var root = new ProbeDatabaseRoot();
        var database = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={root.Path};Pooling=False").Options;
        var version = Guid.NewGuid().ToString(); var key = Guid.NewGuid().ToString(); string receipt;
        var duration = new TextbookMp3Validator().Validate(mp3, options.MaxTextbookDurationSeconds, TimeSpan.FromSeconds(3), default);
        using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
        try
        {
            await using (var db = new VocabularyDbContext(database))
            {
                await db.Database.MigrateAsync();
                db.VocabularyBooks.Add(new() { Id = "content-book", BookName = "Content", Status = false });
                db.VocabularyBookUnits.Add(new() { Id = "content-unit", BookId = "content-book", Number = 1 });
                db.TextbookAudioTracks.Add(new() { Id = "content-track", BookId = "content-book", UnitId = "content-unit", Title = "Content", DisplayOrder = 5, Version = version });
                await db.SaveChangesAsync();
            }
            await using (var host = await Host.Start(database, options))
            {
                using var policy = await host.Client.GetAsync(Root + "/upload-policy");
                using var policyJson = JsonDocument.Parse(await policy.Content.ReadAsStringAsync());
                Check(policy.StatusCode == HttpStatusCode.OK && policyJson.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean(), "Textbook policy must describe the enabled captured configuration.");
                using var uploaded = await host.Put(version, key, mp3); Check(uploaded.StatusCode == HttpStatusCode.OK, "Textbook content HTTP upload must publish the MP3.");
                receipt = await uploaded.Content.ReadAsStringAsync();
                using var payload = JsonDocument.Parse(receipt); var data = payload.RootElement.GetProperty("data");
                Check(data.GetProperty("durationSeconds").GetDouble() == duration && data.EnumerateObject().Count() == 3, "Textbook HTTP receipt must have exact fields and measured PCM duration.");
                using var replay = await host.Put(version, key, mp3); Check(await replay.Content.ReadAsStringAsync() == receipt, "Textbook HTTP retry must replay the original receipt.");
                AudioObjectLocation location;
                await using (var db = new VocabularyDbContext(database))
                {
                    var track = await db.TextbookAudioTracks.SingleAsync();
                    Check(!track.IsAvailable && track.Title == "Content" && track.DisplayOrder == 5 && track.DurationSeconds == duration, "Textbook publication must preserve metadata and start unavailable.");
                    var row = (await new TextbookAudioUploadRepository(db).GetAsync(key, default))!; location = row.Asset.Location;
                    Check(await db.AudioAssets.CountAsync() == 1 && row.Asset.FirstPublishedAtUnixMs != null, "Textbook replay must not allocate another real object.");
                }
                var stat = await store.StatAsync(location, default); Check(stat?.ByteLength == mp3.Length && stat.ContentType == "audio/mpeg", "Real textbook object Stat must match the upload.");
                var link = await store.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(60), default);
                using var http = new HttpClient(); var downloaded = await http.GetByteArrayAsync(link.Url);
                Check(downloaded.SequenceEqual(mp3) && new TextbookMp3Validator().Validate(downloaded, 7200, TimeSpan.FromSeconds(3), default) == duration, "The content-uploaded real MP3 must download and decode completely.");
                var denied = Copy(options); denied.AccessKey = "synthetic-denied-access"; denied.SecretKey = "synthetic-denied-secret";
                await host.Publish(denied);
                var deniedKey = Guid.NewGuid().ToString();
                using var failure = await host.Put(data.GetProperty("version").GetString()!, deniedKey, mp3);
                Check(failure.StatusCode == HttpStatusCode.ServiceUnavailable && !(await failure.Content.ReadAsStringAsync()).Contains("synthetic", StringComparison.Ordinal), "Real S3 denial must return a safe textbook failure.");
                await host.Publish(options);
                using var failedReplay = await host.Put(data.GetProperty("version").GetString()!, deniedKey, mp3);
                Check(failedReplay.StatusCode == HttpStatusCode.ServiceUnavailable, "Textbook denied-upload receipt must remain terminal after credential recovery.");
                using var invalid = await host.Put(data.GetProperty("version").GetString()!, Guid.NewGuid().ToString(), mp3[..^1]);
                Check(invalid.StatusCode == HttpStatusCode.UnsupportedMediaType, "The real HTTP path must reject truncated audio before Reserve.");
            }
            var tighter = Copy(options); tighter.MaxTextbookUploadBytes = 1; tighter.MaxTextbookDurationSeconds = 1;
            await using (var restarted = await Host.Start(database, tighter))
            {
                using var replay = await restarted.Put(version, key, mp3);
                Check(replay.StatusCode == HttpStatusCode.OK && await replay.Content.ReadAsStringAsync() == receipt, "Restart with tighter policy must keep the original textbook receipt.");
                await using var db = new VocabularyDbContext(database);
                Check(await db.TextbookAudioUploadOperations.CountAsync() == 2 && await db.AudioAssets.CountAsync(a => a.FirstPublishedAtUnixMs != null) == 1, "Failed and invalid requests must not replace the prior real file.");
            }
            Console.WriteLine("Private S3 textbook content HTTP upload/PCM/download/replay/denied-write/restart/tighter-policy passed.");
        }
        finally
        {
            await using var db = new VocabularyDbContext(database);
            foreach (var asset in await db.AudioAssets.ToListAsync()) await store.RemoveAsync(new(asset.Bucket, asset.ObjectKey), default);
        }
    }
    private static AudioOptions Copy(AudioOptions options) => JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(options))!;
    private static void Check(bool condition, string message) { if (!condition) throw new ProbeFailure(message); }
    private sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public static async Task<Host> Start(DbContextOptions<VocabularyDbContext> db, AudioOptions options)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options })); builder.Configuration.AddJsonStream(json);
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System); builder.Services.AddAudioStorage(builder.Configuration);
            builder.Services.AddScoped(_ => new VocabularyDbContext(db));
            builder.Services.AddScoped<ITextbookAudioUploadRepository, TextbookAudioUploadRepository>();
            builder.Services.AddScoped<ITextbookAudioTrackRepository, TextbookAudioTrackRepository>();
            builder.Services.AddScoped<ITextbookAudioUploadOperationFactory, TextbookAudioUploadOperationFactory>();
            builder.Services.AddSingleton<ITextbookMp3Validator, TextbookMp3Validator>();
            builder.Services.AddSingleton(new AudioUploadAdmission(2, 8, TimeSpan.FromSeconds(3)));
            builder.Services.AddAuthorization(o => o.AddPolicy(AdminEndpointAuthorization.PolicyName, p => p.RequireRole("admin")));
            var app = builder.Build(); app.Use(async (context, next) =>
            { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "isolated-probe")); context.Response.Headers.CacheControl = "no-store"; await next(context); });
            app.UseAuthorization(); app.MapTextbookAudioUploadEndpoints(); await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(10) });
        }
        public async Task<HttpResponseMessage> Put(string version, string key, byte[] bytes)
        {
            using var body = new MultipartFormDataContent(); body.Add(new StringContent(version), "expectedVersion"); body.Add(new ByteArrayContent(bytes), "file", "tone.mp3");
            using var request = new HttpRequestMessage(HttpMethod.Put, Root + "/content-track/content") { Content = body }; request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }
        public async Task Publish(AudioOptions options)
        {
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }));
            var configuration = new ConfigurationBuilder().AddJsonStream(input).Build();
            using var prepared = await app.Services.GetRequiredService<AudioRuntimeConfiguration>().PrepareAsync(configuration, default);
            Check(prepared is not null, "Textbook probe configuration must publish."); prepared!.Publish();
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
