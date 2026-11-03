using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

/// <summary>Real Host, hosted discovery/execution/recovery and SQLite; only network ports are substituted.</summary>
public sealed class EnrichmentEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string EditRoute = "/admin/vocabulary-books/book/words/word/meanings/meaning";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpSettings_HostedWorker_Adapters_Publication_Edit_Playback_AndRestart(bool native)
    {
        var root = VocabularyWebApplicationFactory.CreateGateSafeDirectory("enrichment-e2e-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(root, "appsettings.json"), "{}");
        var network = new Network(native); var store = new Objects();
        try
        {
            string defaultUk; string defaultUs; string textVersion; string ukVersion; int speechCalls;
            await using (var host = new Host(root, network, store))
            {
                await host.Seed();
                // No configuration/default disabled means no actual provider request.
                await Task.Delay(100, Ct);
                Assert.Empty(network.Requests);
                await host.Settings(new { enrichment = new { audioEnabled = true, scanIntervalSeconds = 1 } });
                await Eventually(async () => (await host.Data("/admin/enrichment/tasks?wordId=word")).ToJsonString().Contains("ConfigurationMissing", StringComparison.Ordinal));
                Assert.Empty(network.Requests);
                await host.Configure(native);
                await Eventually(async () => await host.Bindings() == 2);
                var detail = await host.Data("/admin/vocabulary/word");
                Assert.Equal("liːd", detail["phoneticUk"]!.GetValue<string>());
                Assert.Equal("liːd", detail["phoneticUs"]!.GetValue<string>());
                Assert.Equal("v.", detail["meanings"]![0]!["partOfSpeech"]!.GetValue<string>());
                defaultUk = (await host.Play("en-GB"))["assetId"]!.GetValue<string>();
                defaultUs = (await host.Play("en-US"))["assetId"]!.GetValue<string>();
                Assert.Equal(defaultUk, (await host.Play("en-GB", true))["assetId"]!.GetValue<string>());
                Assert.Equal(2, network.SpeechCount);
                Assert.All(store.Bytes.Values, bytes => Assert.True(Lexarbor.Service.Mp3Validation.IsValid(bytes)));
                // An old override is retained but ignored while the explicit phonetic is empty.
                await host.Legacy(defaultUk);
                Assert.Equal(defaultUk, (await host.Play("en-GB", true))["assetId"]!.GetValue<string>());
                Assert.Equal("default", (await host.Play("en-GB", true))["source"]!.GetValue<string>());
                network.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                network.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await host.Edit("lɛd");
                await network.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
                Assert.Equal("missing", (await host.Play("en-GB", true))["status"]!.GetValue<string>());
                Assert.Equal(defaultUs, (await host.Play("en-US", true))["assetId"]!.GetValue<string>());
                var tasks = await host.Data("/admin/enrichment/tasks?wordId=word&meaningId=meaning&step=Audio");
                Assert.Contains("Running", tasks.ToJsonString(), StringComparison.Ordinal);
                network.Release.SetResult();
                await Eventually(async () => (await host.Play("en-GB", true))["source"]?.GetValue<string>() == "meaning");
                var specific = (await host.Play("en-GB", true))["assetId"]!.GetValue<string>();
                Assert.NotEqual(defaultUk, specific);
                var before = await host.Meaning();
                await host.Edit("[ lɛd ]");
                Assert.Equal(before["expectedUk"]!.ToJsonString(), (await host.Meaning())["expectedUk"]!.ToJsonString());
                Assert.Equal(3, network.SpeechCount);
                await host.Edit(null);
                Assert.Equal(defaultUk, (await host.Play("en-GB", true))["assetId"]!.GetValue<string>());
                Assert.Equal("default", (await host.Meaning())["pronunciation"]!["uk"]!["source"]!.GetValue<string>());
                await Eventually(async () => await host.CleanupObserved(specific));
                Assert.Equal(2, store.Bytes.Count); // Default reference protected the old shared legacy object.
                Assert.Equal(3, network.SpeechCount);
                var summary = await host.Data("/admin/enrichment/summary?wordId=word&meaningId=meaning");
                Assert.Equal(0, summary["applicableTargets"]!.GetValue<int>());
                Assert.Equal(0, summary["missingTargets"]!.GetValue<int>());
                var runtime = host.App.Services.GetRequiredService<EnrichmentRuntimeConfiguration>().Current;
                textVersion = runtime.TextVersion!; ukVersion = runtime.UkTtsVersion!; speechCalls = network.SpeechCount;
            }
            // Same database and persisted file, a new Host/worker/runtime generation.
            await using (var restarted = new Host(root, network, store))
            {
                var runtime = restarted.App.Services.GetRequiredService<EnrichmentRuntimeConfiguration>().Current;
                Assert.Equal(textVersion, runtime.TextVersion); Assert.Equal(ukVersion, runtime.UkTtsVersion);
                Assert.Equal(defaultUk, (await restarted.Play("en-GB", true))["assetId"]!.GetValue<string>());
                Assert.Equal(defaultUs, (await restarted.Play("en-US", true))["assetId"]!.GetValue<string>());
                await Task.Delay(1200, Ct);
                Assert.Equal(speechCalls, network.SpeechCount);
                Assert.Equal(2, await restarted.Bindings());
            }
            Assert.All(network.Requests.Where(r => r.Path != "/v1/chat/completions"), r =>
            {
                Assert.Equal(native ? "/v1/t2a_v2" : "/v1/audio/speech", r.Path);
                Assert.Equal("lead", r.Body[native ? "text" : "input"]!.GetValue<string>());
                if (native)
                {
                    Assert.Equal("hex", r.Body["output_format"]!.GetValue<string>());
                    Assert.Contains(r.Body["voice_setting"]!["voice_id"]!.GetValue<string>(), new[] { "uk", "us" });
                    Assert.StartsWith("lead/(", r.Body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>(), StringComparison.Ordinal);
                }
                else
                {
                    Assert.Equal("mp3", r.Body["response_format"]!.GetValue<string>());
                    Assert.Contains("English", r.Body["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
                    Assert.Contains(r.Body["voice"]!.GetValue<string>(), new[] { "uk", "us" });
                }
            });
            Assert.Contains(network.Requests, r => r.Path != "/v1/chat/completions" && (native ? r.Body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>() : r.Body["instructions"]!.GetValue<string>()).Contains("lɛd", StringComparison.Ordinal));
        }
        finally { network.Release?.TrySetResult(); network.Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnsupportedControlledModel_IsVisibleWithoutTts_AndHttpCorrectionResumesAutomatically()
    {
        var root = VocabularyWebApplicationFactory.CreateGateSafeDirectory("enrichment-capability-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(root, "appsettings.json"), "{}");
        using var network = new Network(false); var store = new Objects();
        try
        {
            await using var host = new Host(root, network, store); await host.Seed();
            await host.Configure(false, "tts-1");
            await Eventually(async () => (await host.Data("/admin/enrichment/tasks?wordId=word&step=Prepare")).ToJsonString().Contains("text.pronunciation_control_unsupported", StringComparison.Ordinal));
            Assert.Equal(0, network.SpeechCount); Assert.Empty(store.Bytes);
            await host.Settings(new { enrichment = new { tts = new { model = "gpt-4o-mini-tts" } } });
            await Eventually(async () => await host.Bindings() == 2);
            Assert.Equal(2, network.SpeechCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalUnbind_WakesHostedGenerationWithoutWaitingForHistoricalScan(bool meaning)
    {
        var root = VocabularyWebApplicationFactory.CreateGateSafeDirectory("enrichment-unbind-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(root, "appsettings.json"), "{}");
        using var network = new Network(true); var store = new Objects();
        try
        {
            await using var host = new Host(root, network, store); await host.Seed(); await host.Configure(true);
            await Eventually(async () => await host.Bindings() == 2);
            if (meaning)
            {
                await host.Edit("lɛd");
                await Eventually(async () => (await host.Play("en-GB", true))["source"]?.GetValue<string>() == "meaning");
            }
            await host.Settings(new { enrichment = new { scanIntervalSeconds = 300 } });
            var old = (await host.Play("en-GB", meaning))["assetId"]!.GetValue<string>();
            var us = (await host.Play("en-US"))["assetId"]!.GetValue<string>();
            var calls = network.SpeechCount;
            var oldObjects = store.Bytes.Keys.ToArray();
            await host.Unbind(old, meaning);
            await Eventually(async () => (await host.Play("en-GB", meaning))["assetId"]?.GetValue<string>() is { } asset && asset != old);
            Assert.Equal(calls + 1, network.SpeechCount);
            Assert.Equal(us, (await host.Play("en-US"))["assetId"]!.GetValue<string>());
            Assert.All(oldObjects, key => Assert.True(store.Bytes.ContainsKey(key))); // Manual DELETE remains logical unbinding.
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(25);
        do { if (await predicate()) return; await Task.Delay(50, Ct); } while (DateTime.UtcNow < until);
        Assert.True(await predicate(), "Hosted worker did not reach the expected persisted/HTTP state.");
    }
    private sealed class Host : IAsyncDisposable
    {
        private readonly VocabularyWebApplicationFactory _base;
        public WebApplicationFactory<Program> App { get; }
        private readonly HttpClient _client;
        public Host(string root, Network network, Objects store)
        {
            _base = new("Testing", true, databasePath: Path.Combine(root, "vocabulary.db"), contentRootPath: root,
                keyContentRoot: Path.Combine(root, "keys"), extraConfiguration: new Dictionary<string, string?>
                {
                    ["Audio:Enabled"] = "true",
                    ["Audio:InternalEndpoint"] = "https://storage.example.test",
                    ["Audio:PublicEndpoint"] = "https://storage.example.test",
                    ["Audio:AccessKey"] = "synthetic-access",
                    ["Audio:SecretKey"] = "synthetic-secret",
                    ["Audio:RecoveryIntervalSeconds"] = "10"
                });
            App = _base.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(network);
                services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(store);
            }));
            _client = App.CreateHttpsClient(); _client.DefaultRequestHeaders.Authorization = new("Bearer", _base.CreateToken("admin"));
        }
        public async Task Seed()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBooks.Add(new() { Id = "book", BookName = "Fixture", Status = true });
            db.Vocabularies.Add(new() { Id = "word", Word = "lead" });
            db.VocabularyMeanings.Add(new() { Id = "meaning", BookId = "book", VocabularyId = "word", Meaning = "guide" });
            db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
            db.VocabularyMeaningUnits.Add(new() { UnitId = "unit", BookId = "book", MeaningId = "meaning", EntryKind = "word" });
            await db.SaveChangesAsync(Ct);
        }
        public Task Configure(bool native, string? model = null) => Settings(new
        {
            enrichment = new
            {
                fieldsEnabled = true,
                audioEnabled = true,
                scanIntervalSeconds = 1,
                text = new { endpoint = "https://planner.example.test/v1", model = "fixture", apiKey = new { action = "replace", value = "synthetic-planner" } },
                tts = new
                {
                    provider = native ? "MiniMaxNative" : "OpenAICompatible",
                    endpoint = native ? "https://api.minimax.io/v1" : "https://api.openai.com/v1",
                    model = model ?? (native ? "speech-2.8-hd" : "gpt-4o-mini-tts"),
                    apiKey = new { action = "replace", value = "synthetic-speech" },
                    uk = new { voice = "uk", speed = 1 },
                    us = new { voice = "us", speed = 1 }
                }
            }
        });
        public async Task Settings(object body)
        {
            using var read = await _client.GetAsync(EnrichmentSettingsEndpoints.Path, Ct); read.EnsureSuccessStatusCode();
            using var request = new HttpRequestMessage(HttpMethod.Put, EnrichmentSettingsEndpoints.Path) { Content = JsonContent.Create(body) };
            request.Headers.IfMatch.Add(read.Headers.ETag!);
            using var response = await _client.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(Ct); Assert.DoesNotContain("synthetic-planner", text); Assert.DoesNotContain("synthetic-speech", text);
        }
        public async Task<JsonNode> Data(string path)
        {
            using var response = await _client.GetAsync(path, Ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!;
        }
        public Task<JsonNode> Play(string locale, bool meaning = false) => Data($"/admin/vocabulary/word/audio/{locale}/playback" + (meaning ? "?meaningId=meaning" : ""));
        public async Task Unbind(string asset, bool meaning)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/admin/vocabulary/word/audio/en-GB" + (meaning ? "?meaningId=meaning" : ""));
            request.Headers.IfMatch.Add(new("\"" + asset + "\""));
            using var response = await _client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        public async Task<JsonNode> Meaning() => (await Data("/admin/vocabulary/word"))["meanings"]![0]!;
        public async Task Edit(string? ipa)
        {
            var current = await Meaning();
            using var response = await _client.PutAsJsonAsync(EditRoute, new { partOfSpeech = "v.", meaning = "guide", example = "", phoneticUk = ipa, expectedUk = current["expectedUk"]!.DeepClone() }, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        public async Task<int> Bindings()
        { using var scope = App.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().VocabularyAudioBindings.CountAsync(Ct); }
        public async Task Legacy(string asset)
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var source = await db.VocabularyAudioBindings.SingleAsync(b => b.AssetId == asset, Ct);
            db.VocabularyAudioBindings.Add(new() { Id = "legacy", WordId = "word", MeaningId = "meaning", Locale = AudioLocale.EnGb, AssetId = asset, SourceRevision = source.SourceRevision });
            await db.SaveChangesAsync(Ct);
        }
        public async Task<bool> CleanupObserved(string asset)
        { using var scope = App.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AudioInvalidations.AnyAsync(a => a.AssetId == asset && a.State == AudioInvalidationState.Observed, Ct); }
        public async ValueTask DisposeAsync() { _client.Dispose(); await App.DisposeAsync(); await _base.DisposeAsync(); }
    }
    private sealed class Network(bool native) : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<(string Path, JsonNode Body)> Requests { get; } = new();
        private int _speechCount;
        public int SpeechCount => Volatile.Read(ref _speechCount);
        public TaskCompletionSource? Entered; public TaskCompletionSource? Release;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var path = request.RequestUri!.AbsolutePath; Requests.Enqueue((path, body));
            if (path == "/v1/chat/completions")
            {
                var input = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
                var required = input["requiredPhonetic"]?.GetValue<string>();
                var ipa = required ?? "liːd";
                var plan = new
                {
                    certain = true,
                    entryKind = "word",
                    classificationBasis = "Fixture word position",
                    spokenText = "lead",
                    requiredPhonetic = required,
                    phonetic = ipa,
                    partsOfSpeech = new[] { new { meaningId = "meaning", partOfSpeech = "v." } },
                    pronunciationHint = native ? ipa : "Use the required reading.",
                    pronunciationControl = native ? "ipa" : "instructions",
                    speechPhonetic = native ? ipa : null
                };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(plan) } } } }) };
            }
            Assert.Equal(native ? "api.minimax.io" : "api.openai.com", request.RequestUri.Host);
            Interlocked.Increment(ref _speechCount);
            if (Release is { } release) { Entered!.TrySetResult(); await release.Task.WaitAsync(token); }
            var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"), token);
            return new(HttpStatusCode.OK) { Content = native ? JsonContent.Create(new { base_resp = new { status_code = 0 }, data = new { status = 2, audio = Convert.ToHexString(bytes) } }) : new ByteArrayContent(bytes) };
        }
    }
    private sealed class Objects : IAudioObjectStoreFactory, IAudioObjectStore
    {
        public ConcurrentDictionary<string, byte[]> Bytes { get; } = new();
        public IAudioObjectStore Create(AudioOptions options) => this;
        public async Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token)
        { using var output = new MemoryStream(); await content.CopyToAsync(output, token); Bytes[location.Key] = output.ToArray(); }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => Task.FromResult(Bytes.TryGetValue(location.Key, out var bytes) ? new AudioObjectInfo(bytes.Length, "audio/mpeg") : null);
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) { Bytes.TryRemove(location.Key, out _); return Task.CompletedTask; }
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => Task.FromResult(new AudioPlaybackLink("https://storage.example.test/" + location.Key, DateTimeOffset.UtcNow.Add(validity)));
    }
}
