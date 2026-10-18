using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class TextbookPlaybackProbe
{
    private const string PublicRoute = "/api/vocabulary-books/playback-book/units/playback-unit/audio-tracks/playback-track/playback";
    private const string PublicList = "/api/vocabulary-books/playback-book/units/playback-unit/audio-tracks";
    private const string Route = "/admin/vocabulary-books/playback-book/units/playback-unit/audio-tracks/playback-track/playback";
    public static async Task VerifyAsync(AudioOptions options, byte[] bytes)
    {
        using var root = new ProbeDatabaseRoot();
        var database = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={root.Path};Pooling=False").Options;
        using var realStore = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
        var location = new AudioObjectLocation(options.Bucket, $"audio/{Guid.NewGuid():N}.mp3");
        using (var stream = new MemoryStream(bytes)) await realStore.PutAsync(location, stream, bytes.Length, "audio/mpeg", default);
        try
        {
            await using (var db = new VocabularyDbContext(database))
            {
                await db.Database.MigrateAsync();
                // Empty disabled book and unavailable track are intentionally playable by an administrator.
                db.VocabularyBooks.Add(new() { Id = "playback-book", BookName = "Playback", Status = false });
                db.VocabularyBookUnits.Add(new() { Id = "playback-unit", BookId = "playback-book", Number = 1 });
                db.AudioAssets.Add(new() { Id = "playback-asset", Bucket = location.Bucket, ObjectKey = location.Key, Sha256 = new string('a', 64), ByteLength = bytes.Length, CreatedAtUnixMs = 1, FirstPublishedAtUnixMs = 2 });
                db.TextbookAudioTracks.Add(new() { Id = "playback-track", BookId = "playback-book", UnitId = "playback-unit", Title = "Playback", CurrentAssetId = "playback-asset", DurationSeconds = 1, Version = Guid.NewGuid().ToString("D") });
                await db.SaveChangesAsync();
            }
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            builder.Services.AddScoped(_ => new VocabularyDbContext(database));
            builder.Services.AddScoped<ITextbookAudioPlaybackReadRepository, TextbookAudioPlaybackReadRepository>();
            var store = new PlaybackProbeStore(realStore);
            var runtimeOptions = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(options))!;
            runtimeOptions.PlaybackTtlSeconds = 60;
            builder.Services.AddSingleton(new AudioRuntimeConfiguration(Options.Create(runtimeOptions), new PlaybackProbeFactory(store)));
            builder.Services.AddScoped<ITextbookAudioPlaybackOperationFactory, TextbookAudioPlaybackOperationFactory>();
            builder.Services.AddAuthorization(o => o.AddPolicy(AdminEndpointAuthorization.PolicyName, p => p.RequireRole("admin")));
            await using var app = builder.Build();
            app.Use(async (context, next) => { try { await next(context); } catch (Lexarbor.Domain.Exceptions.BusinessRuleException) { context.Response.StatusCode = 422; } catch (Lexarbor.Domain.Exceptions.ResourceNotFoundException) { context.Response.StatusCode = 404; } });
            app.Use(async (context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "isolated-probe")); context.Response.Headers.CacheControl = "no-store"; await next(context); });
            app.UsePublicTextbookAudioNoStore(); app.UseAuthorization(); app.MapTextbookAudioPlaybackEndpoints(); app.MapPublicTextbookAudioEndpoints(); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(10) };
            using var downloadClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            async Task<AudioPlaybackLink> Link(string route = Route)
            {
                using var response = await client.GetAsync(route);
                Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Textbook playback must return no-store success.");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement.GetProperty("data");
                Check(data.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "expiresAt", "trackId", "url" }), "Textbook playback must return only its three public fields.");
                Check(data.GetProperty("trackId").GetString() == "playback-track", "Textbook playback must select its own track.");
                var expires = data.GetProperty("expiresAt").GetDateTimeOffset(); Check(expires.Offset == TimeSpan.Zero, "Textbook expiry must be UTC.");
                return new(data.GetProperty("url").GetString()!, expires);
            }
            async Task Unavailable(string route = Route)
            {
                using var response = await client.GetAsync(route);
                Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.CacheControl?.NoStore == true &&
                    await response.Content.ReadAsStringAsync() == "{\"success\":false,\"message\":\"Audio storage is unavailable.\"}", "Textbook lost/denied objects must return a safe no-store 503.");
            }
            _ = await Link(); // Administrator can preview disabled/unavailable content.
            // Exercise the real public mapper and SQLite authorization before S3.
            async Task State(string sql)
            {
                await using var db = new VocabularyDbContext(database);
                await db.Database.ExecuteSqlRawAsync(sql);
            }
            async Task Refused(HttpStatusCode expected)
            {
                foreach (var route in new[] { PublicList, PublicRoute })
                {
                    using var response = await client.GetAsync(route);
                    Check(response.StatusCode == expected && response.Headers.CacheControl?.NoStore == true, "Public textbook authorization must reject safely before storage.");
                }
            }
            await Refused(HttpStatusCode.UnprocessableEntity);
            await State("UPDATE vocabulary_book SET status=1");
            using (var hidden = await client.GetAsync(PublicRoute)) Check(hidden.StatusCode == HttpStatusCode.NotFound, "Unavailable track must not be public.");
            await State("UPDATE textbook_audio_track SET is_available=1");
            using (var listed = await client.GetAsync(PublicList))
            {
                Check(listed.StatusCode == HttpStatusCode.OK && listed.Headers.CacheControl?.NoStore == true, "Public textbook metadata must succeed.");
                using var json = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
                var item = json.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().Single();
                Check(item.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "bookId", "displayOrder", "durationSeconds", "id", "title", "unitId", "version" }), "Public textbook list must have seven safe fields.");
            }
            async Task VerifyMedia(string route, string suffix)
            {
                var link = await Link(route);
                using (var response = await downloadClient.GetAsync(link.Url))
                {
                    Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "audio/mpeg", "Textbook signed download must serve MP3 MIME.");
                    var downloaded = await response.Content.ReadAsByteArrayAsync(); Check(downloaded.SequenceEqual(bytes), "Textbook signed download must match its MP3.");
                    await File.WriteAllBytesAsync(Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_DOWNLOAD")! + suffix, downloaded);
                }
                using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, link.Url))
                {
                    rangeRequest.Headers.Range = new RangeHeaderValue(10, 41); using var range = await downloadClient.SendAsync(rangeRequest);
                    Check(range.StatusCode == HttpStatusCode.PartialContent && (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[10..42]), "Textbook signed URL must support actual Range.");
                }
                using var expiredStore = new S3AudioObjectStore(Options.Create(options), new OffsetClock());
                store.Inner = expiredStore; var expired = await Link(route);
                using (var response = await downloadClient.GetAsync(expired.Url)) Check(response.StatusCode == HttpStatusCode.Forbidden, "S3 must reject the expired textbook signature.");
                store.Inner = realStore; var renewed = await Link(route);
                Check((await downloadClient.GetByteArrayAsync(renewed.Url)).SequenceEqual(bytes), "Textbook endpoint must renew the expired URL.");
                var deniedOptions = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(options))!;
                deniedOptions.AccessKey = "synthetic-denied-access"; deniedOptions.SecretKey = "synthetic-denied-secret";
                using var denied = new S3AudioObjectStore(Options.Create(deniedOptions), TimeProvider.System);
                store.Inner = denied; await Unavailable(route); store.Inner = realStore;
            }
            await VerifyMedia(Route, ".textbook-admin.mp3");
            await VerifyMedia(PublicRoute, ".textbook.mp3");
            await realStore.RemoveAsync(location, default); await Unavailable(); await Unavailable(PublicRoute);
            Console.WriteLine("Private S3 administrator and public textbook endpoints authorization/list/GET/MIME/Range/expiry/renewal/denial/missing and decode download passed.");
        }
        finally { await realStore.RemoveAsync(location, default); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new ProbeFailure(message); }
}
