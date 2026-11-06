using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentSettingsTests
{
    private const string PreviewDraft = """{"locale":"uk","sample":"sentence","tts":{"provider":"MiniMaxNative","endpoint":"https://api.minimax.io/v1","model":"speech-2.8-hd","apiKey":{"action":"replace","value":"synthetic-preview-only-key"},"uk":{"voice":"draft-uk","speed":0.9,"emotion":"happy","pitch":2,"volume":0.8},"us":{"voice":"draft-us","speed":1.1,"emotion":"calm"}}}""";
    private static byte[] PreviewMp3 => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    private static async Task<HttpResponseMessage> Preview(HttpClient client, string? revision, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechPreviewEndpoints.Path)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (revision != null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        return await client.SendAsync(request, Ct);
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("us")]
    public async Task Preview_UsesUnsavedLocaleAndCredentials_WithoutConfigurationStorageOrWorkers(string locale)
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var initial = await Read(client);
        var runtime = factory.Services.GetRequiredService<EnrichmentRuntimeConfiguration>();
        var snapshot = runtime.Current;
        using var response = await Preview(client, Revision(initial), PreviewDraft.Replace("\"locale\":\"uk\"", $"\"locale\":\"{locale}\""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PreviewMp3, await response.Content.ReadAsByteArrayAsync(Ct));
        var body = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal("draft-" + locale, body["voice_setting"]!["voice_id"]!.GetValue<string>());
        Assert.Equal(locale == "uk" ? "happy" : "calm", body["voice_setting"]!["emotion"]!.GetValue<string>());
        Assert.Contains(locale == "uk" ? "aʊə" : "aʊɚ", body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>());
        Assert.Equal("synthetic-preview-only-key", Assert.Single(handler.Keys));
        Assert.Same(snapshot, runtime.Current); Assert.False(File.Exists(FilePath));
        Assert.Equal(initial.ToJsonString(), (await Read(client)).ToJsonString());
    }

    [Fact]
    public async Task Preview_KeepAndReplaceCredentials_UseCapturedSettingsWithoutPersistingDraft()
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        using var saved = await Put(client, Revision(await Read(client)), """{"enrichment":{"tts":{"apiKey":{"action":"replace","value":"synthetic-saved-preview-key"}}}}""");
        var before = File.ReadAllBytes(FilePath); var revision = Revision(await Read(client));
        var keep = PreviewDraft.Replace("\"action\":\"replace\",\"value\":\"synthetic-preview-only-key\"", "\"action\":\"keep\"");
        Assert.Equal(HttpStatusCode.OK, (await Preview(client, revision, keep)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Preview(client, revision, PreviewDraft)).StatusCode);
        Assert.Equal(new[] { "synthetic-saved-preview-key", "synthetic-preview-only-key" }, handler.Keys);
        var clear = PreviewDraft.Replace("\"action\":\"replace\",\"value\":\"synthetic-preview-only-key\"", "\"action\":\"clear\"");
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(client, revision, clear)).StatusCode);
        Assert.Equal(2, handler.Bodies.Count); Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Theory]
    [InlineData("{\"locale\":\"xx\",\"sample\":\"word\",\"tts\":{}}")]
    [InlineData("{\"locale\":\"uk\",\"sample\":\"custom\",\"tts\":{}}")]
    [InlineData("{\"locale\":\"uk\",\"sample\":\"word\",\"tts\":{},\"text\":\"custom\"}")]
    [InlineData("{\"locale\":\"uk\",\"locale\":\"us\",\"sample\":\"word\",\"tts\":{}}")]
    [InlineData("{\"locale\":\"uk\",\"sample\":\"word\",\"tts\":{\"uk\":{\"pitch\":13}}}")]
    [InlineData("{\"locale\":\"uk\",\"sample\":\"word\",\"tts\":{\"uk\":{\"emotion\":\"whisper\"}}}")]
    [InlineData("{\"locale\":\"uk\",\"sample\":\"word\",\"tts\":{\"uk\":{\"volume\":0}}}")]
    public async Task Preview_RejectsInvalidBodyWithoutDispatch(string body)
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(client, Revision(await Read(client)), body)).StatusCode);
        Assert.Empty(handler.Bodies); Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Preview_RequiresRevisionAndBoundedBody_AndRejectsUnknownEndpoint()
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var revision = Revision(await Read(client));
        Assert.Equal((HttpStatusCode)428, (await Preview(client, null, PreviewDraft)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(client, "invalid", PreviewDraft)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Preview(client, "\"00000000000000000000000000000000\"", PreviewDraft)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Preview(client, revision, new string(' ', 16385))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(client, revision, PreviewDraft.Replace("api.minimax.io", "unknown.example.test"))).StatusCode);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Preview_AdminCookieCsrfAndBearerPriority_ArePreserved()
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient();
        var revision = (await factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().ReadAsync(Ct)).Revision;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Preview(client, revision, PreviewDraft)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("user"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Preview(client, revision, PreviewDraft)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("Cookie", baseFactory.CreateSessionCookie());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Preview(client, revision, PreviewDraft)).StatusCode);
        AdminTestAntiforgery.Attach(client, baseFactory);
        Assert.Equal(HttpStatusCode.OK, (await Preview(client, revision, PreviewDraft)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-synthetic-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Preview(client, revision, PreviewDraft)).StatusCode);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("bad-audio", 502)]
    [InlineData("throttled", 429)]
    [InlineData("credentials", 400)]
    public async Task Preview_ProviderFailuresAreSafeAndNotRetried(string failure, int status)
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler { Failure = failure };
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        using var response = await Preview(client, Revision(await Read(client)), PreviewDraft);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.DoesNotContain("synthetic-preview-only-key", await response.Content.ReadAsStringAsync(Ct));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task SavedStyles_PersistAndAdvanceOnlyTheirLocaleVersion()
    {
        string? uk, us;
        using (var factory = Factory())
        using (var client = Admin(factory))
        {
            using var initialized = await Put(client, Revision(await Read(client)), """{"enrichment":{"concurrency":2}}""");
            var before = Snapshot(factory);
            using var saved = await Put(client, Revision(await Read(client)), """{"enrichment":{"tts":{"uk":{"style":"Clear and calm.","emotion":"calm","pitch":-2,"volume":0.8}}}}""");
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var current = Snapshot(factory);
            Assert.NotEqual(before.UkTtsVersion, current.UkTtsVersion);
            Assert.Equal(before.UsTtsVersion, current.UsTtsVersion); Assert.Equal(before.TextVersion, current.TextVersion);
            Assert.Equal(before.TtsServiceVersion, current.TtsServiceVersion);
            Assert.Equal("Clear and calm.", current.Enrichment.Uk.Style); Assert.Equal("", current.Enrichment.Us.Style);
            uk = current.UkTtsVersion; us = current.UsTtsVersion;
        }
        using var restarted = Factory(); using var admin = Admin(restarted); await Read(admin);
        Assert.Equal(uk, Snapshot(restarted).UkTtsVersion); Assert.Equal(us, Snapshot(restarted).UsTtsVersion);
        Assert.Equal("calm", Snapshot(restarted).Enrichment.Uk.Emotion);
    }

    [Fact]
    public async Task Preview_DeploymentOverridesAreEnforced()
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler();
        using var factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.Add(new OverrideSource()));
            b.ConfigureServices(s => s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.Conflict, (await Preview(client, Revision(await Read(client)), PreviewDraft)).StatusCode);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Preview_ConcurrentRequestsAreRejected_AndCompletionReleasesAdmission()
    {
        using var baseFactory = Factory(); using var handler = new PreviewHandler { Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(PronunciationSpeechClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var revision = Revision(await Read(client));
        var first = Preview(client, revision, PreviewDraft);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var rejected = await Preview(client, revision, PreviewDraft);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode); Assert.NotNull(rejected.Headers.RetryAfter);
        handler.Wait.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Preview(client, revision, PreviewDraft)).StatusCode);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public void LegacyConfigurationVersions_SurviveAddingDefaultStyleFields()
    {
        var config = new ConfigurationBuilder().Build(); using var lifetime = config as IDisposable;
        var legacyFields = EnrichmentConfigurationFields.Read(config).Where(p =>
            !p.Key.EndsWith(":Style", StringComparison.Ordinal) && !p.Key.EndsWith(":Emotion", StringComparison.Ordinal)
            && !p.Key.EndsWith(":Pitch", StringComparison.Ordinal) && !p.Key.EndsWith(":Volume", StringComparison.Ordinal)).ToArray();
        var key = RandomNumberGenerator.GetBytes(32);
        var metadata = new JsonObject { ["Key"] = Convert.ToBase64String(key) };
        var expected = new Dictionary<string, string>();
        foreach (var name in new[] { "Text", "Uk", "Us", "TtsService" })
        {
            var selected = legacyFields.Where(p => name == "Text" ? p.Key.StartsWith("Enrichment:Text:", StringComparison.Ordinal)
                : p.Key.StartsWith("Enrichment:Tts:", StringComparison.Ordinal)
                    && (!p.Key.StartsWith("Enrichment:Tts:Uk:", StringComparison.Ordinal) && !p.Key.StartsWith("Enrichment:Tts:Us:", StringComparison.Ordinal)
                        || name != "TtsService" && p.Key.StartsWith("Enrichment:Tts:" + name + ":", StringComparison.Ordinal)))
                .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
            var version = Guid.NewGuid().ToString("N"); expected[name] = version;
            metadata[name] = new JsonObject
            {
                ["Version"] = version,
                ["Digest"] = Convert.ToBase64String(HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(selected)))
            };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, new JsonObject { ["EnrichmentConfigurationVersions"] = metadata }.ToJsonString());
        var snapshot = new EnrichmentRuntimeConfiguration(config, new PersistentConfigurationFileStore(_root)).Current;
        Assert.True(snapshot.ConfigurationReady);
        Assert.Equal(expected["Text"], snapshot.TextVersion); Assert.Equal(expected["Uk"], snapshot.UkTtsVersion);
        Assert.Equal(expected["Us"], snapshot.UsTtsVersion); Assert.Equal(expected["TtsService"], snapshot.TtsServiceVersion);
    }

    private sealed class PreviewHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> Keys { get; } = [];
        public string? Failure { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Wait { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken)); Keys.Add(request.Headers.Authorization!.Parameter!);
            Entered.TrySetResult();
            if (Wait != null) await Wait.Task.WaitAsync(cancellationToken);
            if (Failure == "throttled") return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("synthetic-preview-only-key") };
            if (Failure == "credentials") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("synthetic-preview-only-key") };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { base_resp = new { status_code = 0 }, data = new { status = 2, audio = Failure == "bad-audio" ? "FFFF" : Convert.ToHexString(PreviewMp3) } }) };
        }
    }
}
