using System.Net;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class AudioUnbindEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Store : IAudioObjectStore
    {
        public int Puts;
        public Func<CancellationToken, Task>? BeforePut;
        public readonly Dictionary<string, byte[]> Objects = [];
        public async Task PutAsync(AudioObjectLocation location, Stream input, long length, string type, CancellationToken token)
        {
            Puts++;
            if (BeforePut != null) await BeforePut(token);
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes, token);
            Objects.Add(location.Key, bytes.ToArray());
        }
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new InvalidOperationException("Delete must not call S3.");
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => throw new InvalidOperationException("Delete must not call S3.");
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan ttl, CancellationToken token) => throw new InvalidOperationException("Delete must not call S3.");
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly Store Store = new();
        public readonly VocabularyWebApplicationFactory Factory;
        public readonly WebApplicationFactory<Program> App;
        public readonly HttpClient Client;
        public Host(bool enabled = true)
        {
            Factory = new("Testing", true, extraConfiguration: new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = enabled.ToString(),
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:PublicEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:AccessKey"] = "synthetic",
                ["Audio:SecretKey"] = "synthetic"
            });
            App = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAudioObjectStore>(); services.AddSingleton<IAudioObjectStore>(Store);
            }));
            Client = App.CreateClient(new() { BaseAddress = new("https://localhost") });
            Client.DefaultRequestHeaders.Authorization = new("Bearer", Factory.CreateToken("admin"));
        }
        public async Task SeedAsync()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.AddRange(new() { Id = "w", Word = "take off", PhoneticUk = "uk", PhoneticUs = "us" }, new() { Id = "other", Word = "other" });
            db.VocabularyBooks.Add(new() { Id = "disabled", BookName = "Disabled", Status = false });
            db.VocabularyMeanings.AddRange(new() { Id = "m", VocabularyId = "w", BookId = "disabled", PartOfSpeech = "v.", Meaning = "leave" },
                new() { Id = "other-m", VocabularyId = "other", BookId = "disabled", Meaning = "other" });
            await db.SaveChangesAsync(Ct);
        }
        public async Task<(string Asset, string Revision, string Key)> Upload(string locale = "en-GB", string? meaning = null, string? expected = null,
            string? revision = null, string? key = null)
        {
            if (revision == null)
            {
                using var scope = App.Services.CreateScope();
                var snapshot = await scope.ServiceProvider.GetRequiredService<IAudioReadRepository>().GetSnapshotAsync("w", meaning, Ct);
                revision = AudioSelectionService.Select(snapshot, AudioLocales.FromCode(locale)).SourceRevision;
            }
            key ??= Guid.NewGuid().ToString();
            using var request = UploadRequest(locale, meaning, expected, revision, key);
            using var response = await Client.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return (json.RootElement.GetProperty("data").GetProperty("assetId").GetString()!, revision, key);
        }
        public HttpRequestMessage UploadRequest(string locale, string? meaning, string? expected, string revision, string key)
        {
            var body = new MultipartFormDataContent(); body.Add(new StringContent(revision), "sourceRevision");
            body.Add(new StringContent(expected ?? ""), "expectedAssetId");
            body.Add(new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"))), "file", "tone.mp3");
            var request = new HttpRequestMessage(HttpMethod.Put, Route(locale, meaning)) { Content = body };
            request.Headers.Add("Idempotency-Key", key); return request;
        }
        public async Task<string> DurableHistory()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            return JsonSerializer.Serialize(new
            {
                assets = await db.AudioAssets.AsNoTracking().OrderBy(x => x.Id).ToListAsync(Ct),
                operations = await db.AudioUploadOperations.AsNoTracking().OrderBy(x => x.IdempotencyKey).ToListAsync(Ct)
            });
        }
        public async Task<string?> Own(string locale = "en-GB", string? meaning = null)
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            return await db.VocabularyAudioBindings.Where(x => x.WordId == "w" && x.MeaningId == meaning && x.Locale == AudioLocales.FromCode(locale)).Select(x => x.AssetId).SingleOrDefaultAsync(Ct);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); await Factory.DisposeAsync(); }
    }
    private static string Route(string locale, string? meaning) => "/admin/vocabulary/w/audio/" + locale + (meaning == null ? "" : "?meaningId=" + meaning);
    private static async Task<HttpResponseMessage> Delete(Host host, string? asset, string locale = "en-GB", string? meaning = null, string? raw = null, string? path = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path ?? Route(locale, meaning));
        if (raw != null || asset != null) request.Headers.TryAddWithoutValidation("If-Match", raw ?? "\"" + asset + "\"");
        return await host.Client.SendAsync(request, Ct);
    }

    [Theory]
    [InlineData("en-GB", null)]
    [InlineData("en-US", null)]
    [InlineData("en-GB", "m")]
    [InlineData("en-US", "m")]
    public async Task DeleteOnlyOwnTarget_RetainsOtherBindingsObjectsAndImmutableReceipts(string locale, string? meaning)
    {
        await using var host = new Host(); await host.SeedAsync();
        var uploads = new Dictionary<(string, string), (string Asset, string Revision, string Key)>();
        foreach (var accent in new[] { "en-GB", "en-US" }) foreach (var target in new string?[] { null, "m" })
            uploads.Add((accent, target ?? ""), await host.Upload(accent, target));
        var selected = uploads[(locale, meaning ?? "")]; var before = await host.DurableHistory();
        using var removed = await Delete(host, selected.Asset, locale, meaning); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var json = JsonDocument.Parse(await removed.Content.ReadAsStringAsync(Ct)); Assert.True(json.RootElement.GetProperty("data").GetProperty("removed").GetBoolean());
        foreach (var item in uploads) Assert.Equal(item.Key == (locale, meaning ?? "") ? null : item.Value.Asset, await host.Own(item.Key.Item1, item.Key.Item2 == "" ? null : item.Key.Item2));
        Assert.Equal(before, await host.DurableHistory()); Assert.Equal(4, host.Store.Objects.Count);
        using var metadata = await host.Client.GetAsync("/admin/vocabulary/w/audio?meaningId=m", Ct);
        using var meta = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(Ct));
        var result = meta.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().Single(x => x.GetProperty("locale").GetString() == locale);
        Assert.Equal(meaning == null ? "meaning" : "default", result.GetProperty("source").GetString());
        using var repeated = await Delete(host, selected.Asset, locale, meaning); Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        var replay = await host.Upload(locale, meaning, revision: selected.Revision, key: selected.Key); Assert.Equal(selected.Asset, replay.Asset);
        Assert.Null(await host.Own(locale, meaning)); Assert.Equal(before, await host.DurableHistory()); Assert.Equal(4, host.Store.Puts);
    }

    [Theory]
    [InlineData("missing", 400)]
    [InlineData("bare", 400)]
    [InlineData("weak", 400)]
    [InlineData("wildcard", 400)]
    [InlineData("list", 400)]
    [InlineData("multiple", 400)]
    [InlineData("uppercase", 400)]
    [InlineData("invalid", 400)]
    [InlineData("empty", 400)]
    [InlineData("locale", 400)]
    [InlineData("blank-meaning", 400)]
    [InlineData("wrong-asset", 409)]
    [InlineData("wrong-ownership", 404)]
    [InlineData("missing-word", 404)]
    [InlineData("fallback-only", 404)]
    [InlineData("missing-meaning", 404)]
    public async Task InvalidConditionsOrTargets_AreSafeAndDoNotChangeBindingsOrHistory(string kind, int status)
    {
        await using var host = new Host(); await host.SeedAsync(); var first = await host.Upload(); var before = await host.DurableHistory();
        var tag = "\"" + first.Asset + "\"";
        var raw = kind switch
        {
            "bare" => first.Asset,
            "weak" => "W/" + tag,
            "wildcard" => "*",
            "list" => tag + ", " + tag,
            "uppercase" => tag.ToUpperInvariant(),
            "invalid" => "\"synthetic-secret-key\"",
            "empty" => "",
            "wrong-asset" => "\"" + Guid.NewGuid() + "\"",
            _ => tag
        };
        var path = kind switch
        {
            "locale" => Route("en-AU", null),
            "blank-meaning" => Route("en-GB", ""),
            "wrong-ownership" => Route("en-GB", "other-m"),
            "missing-word" => "/admin/vocabulary/absent/audio/en-GB",
            "fallback-only" => Route("en-GB", "m"),
            "missing-meaning" => Route("en-GB", "absent"),
            _ => Route("en-GB", null)
        };
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        if (kind != "missing") request.Headers.TryAddWithoutValidation("If-Match", raw);
        if (kind == "multiple") request.Headers.TryAddWithoutValidation("If-Match", tag);
        using var response = await host.Client.SendAsync(request, Ct); Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain("synthetic", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(first.Asset, await host.Own()); Assert.Equal(before, await host.DurableHistory()); Assert.Single(host.Store.Objects);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie", 401)]
    [InlineData("student-cookie", 403)]
    [InlineData("invalid-bearer", 401)]
    [InlineData("cookie-invalid-csrf", 401)]
    public async Task AuthenticationPrecedesInvalidInput_AndBearerWins(string identity, int status)
    {
        await using var host = new Host(); await host.SeedAsync(); var upload = await host.Upload();
        host.Client.DefaultRequestHeaders.Authorization = identity is "student" or "student-cookie" ? new("Bearer", host.Factory.CreateToken("student"))
            : identity == "invalid-bearer" ? new("Bearer", "invalid") : null;
        if (identity != "anonymous" && identity != "student") host.Client.DefaultRequestHeaders.Add("Cookie", host.Factory.CreateSessionCookie("admin"));
        if (identity == "cookie-invalid-csrf") host.Client.DefaultRequestHeaders.Add(AdminTestAntiforgery.HeaderName, "invalid");
        using var response = await Delete(host, null, locale: "invalid"); Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(upload.Asset, await host.Own());
    }

    [Fact]
    public async Task ValidCookieCsrfAndBearerWithCookie_CanDelete_AndAdminRoutesKeepExistingLimits()
    {
        await using var host = new Host(); await host.SeedAsync(); var gb = await host.Upload(); var us = await host.Upload("en-US");
        var cookie = host.Factory.CreateSessionCookie("admin"); host.Client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var bearer = await Delete(host, gb.Asset); Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = null;
        using var csrf = await host.Client.GetAsync("/admin/auth/csrf", Ct); csrf.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync(Ct)); host.Client.DefaultRequestHeaders.Remove("Cookie");
        host.Client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + csrf.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        host.Client.DefaultRequestHeaders.Add(AdminTestAntiforgery.HeaderName, json.RootElement.GetProperty("token").GetString());
        using var session = await Delete(host, us.Asset, "en-US"); Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        for (var i = 0; i < 10; i++) { using var repeat = await Delete(host, us.Asset, "en-US"); Assert.Equal(HttpStatusCode.NotFound, repeat.StatusCode); }
        using var metadata = await host.Client.GetAsync("/admin/vocabulary/w/audio", Ct); metadata.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DisabledAudio_IsSafe503AfterAuthorization_AndMakesNoStorageCall()
    {
        await using var host = new Host(false); await host.SeedAsync();
        using var response = await Delete(host, Guid.NewGuid().ToString()); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"success\":false,\"message\":\"Audio storage is disabled.\"}", await response.Content.ReadAsStringAsync(Ct));
        host.Client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await Delete(host, null); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); Assert.Empty(host.Store.Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementAndDelete_HaveOnlyTheMatchingWinner(bool replacementFirst)
    {
        await using var host = new Host(); await host.SeedAsync(); var first = await host.Upload();
        if (replacementFirst)
        {
            var newer = await host.Upload(expected: first.Asset); using var conflict = await Delete(host, first.Asset);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Equal(newer.Asset, await host.Own());
        }
        else
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Store.BeforePut = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
            using var request = host.UploadRequest("en-GB", null, first.Asset, first.Revision, Guid.NewGuid().ToString());
            var replacing = host.Client.SendAsync(request, Ct); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            using var removed = await Delete(host, first.Asset); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            release.SetResult(); using var conflicted = await replacing; Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode); Assert.Null(await host.Own());
            using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Single(await db.AudioUploadOperations.Where(x => x.State == AudioUploadState.Abandoned && x.ReceiptStatusCode == 409).ToListAsync(Ct));
        }
        Assert.Equal(2, host.Store.Objects.Count);
    }
}
