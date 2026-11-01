using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Lexarbor.Service.Tests;

public sealed class EnrichmentSettingsTests : IDisposable
{
    private const string Url = EnrichmentSettingsEndpoints.Path;
    private const string Secret = "synthetic-enrichment-key-marker";
    private readonly string _root = VocabularyWebApplicationFactory.CreateGateSafeDirectory("enrichment-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string FilePath => Path.Combine(_root, "data", "appsettings.json");
    public EnrichmentSettingsTests() => File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{\"Unknown\":{\"keep\":42},\"Audio\":{\"SecretKey\":\"synthetic-existing-s3-key\"}}");
    private VocabularyWebApplicationFactory Factory(Dictionary<string, string?>? config = null) => new("Testing", true, contentRootPath: _root, extraConfiguration: config);
    private static HttpClient Admin(VocabularyWebApplicationFactory factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin")); return client;
    }
    private static async Task<JsonNode> Read(HttpClient client)
    {
        using var response = await client.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync(Ct); Assert.DoesNotContain(Secret, text); Assert.DoesNotContain("Digest", text); Assert.DoesNotContain("EnrichmentConfigurationVersions", text);
        var body = JsonNode.Parse(text)!; Assert.Equal(response.Headers.ETag!.ToString(), body["revision"]!.GetValue<string>()); return body;
    }
    private static async Task<HttpResponseMessage> Put(HttpClient client, string? revision, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        var response = await client.SendAsync(request, Ct);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(Ct)); return response;
    }
    private static string Revision(JsonNode body) => body["revision"]!.GetValue<string>();
    private static EnrichmentConfigurationSnapshot Snapshot(VocabularyWebApplicationFactory factory) => factory.Services.GetRequiredService<EnrichmentRuntimeConfiguration>().Current;

    [Fact]
    public async Task Defaults_GetDoesNotCreateFile_AndAudioOnlyRequiresPreparationAndEachLocale()
    {
        using var factory = Factory(); using var client = Admin(factory);
        var initial = await Read(client); var original = Snapshot(factory);
        Assert.False(File.Exists(FilePath)); Assert.False(original.ConfigurationReady); Assert.False(original.CanPrepare);
        Assert.False(initial["enrichment"]!["fieldsEnabled"]!.GetValue<bool>());
        Assert.Equal("MiniMaxNative", original.Enrichment.Provider); Assert.Equal("speech-2.8-hd", original.Enrichment.Tts.Model);
        Assert.Null(original.Enrichment.Uk.Speed); Assert.Equal("", original.Enrichment.Us.Voice);
        var runtime = factory.Services.GetRequiredService<EnrichmentRuntimeConfiguration>();
        var changed = runtime.WaitForChangeAsync(original, Ct);
        using var saved = await Put(client, Revision(initial), """{"enrichment":{"audioEnabled":true,"tts":{"endpoint":"https://tts.example.test/v1","apiKey":{"action":"replace","value":"synthetic-enrichment-key-marker"},"uk":{"voice":"uk-fixture","speed":1}}}}""");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var audioOnly = await changed; Assert.True(audioOnly.ConfigurationReady); Assert.False(audioOnly.CanPrepare); Assert.False(audioOnly.HasTtsConfiguration("uk"));
        var state = await Read(client);
        using var prepared = await Put(client, Revision(state), """{"enrichment":{"text":{"endpoint":"https://text.example.test/v1","model":"planner-fixture","apiKey":{"action":"replace","value":"synthetic-enrichment-key-marker"}}}}""");
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var current = Snapshot(factory); Assert.True(current.CanPrepare); Assert.True(current.HasTtsConfiguration("uk")); Assert.False(current.HasTtsConfiguration("us"));
        Assert.False(current.Enrichment.FieldsEnabled); Assert.False(original.Enrichment.AudioEnabled);
        var file = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.Equal(42, file["Unknown"]!["keep"]!.GetValue<int>()); Assert.Equal("synthetic-existing-s3-key", file["Audio"]!["SecretKey"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public async Task Versions_OnlyRelevantStepOrLocaleChanges_AndSurviveRestart()
    {
        string? text; string? uk; string? us; string etag;
        using (var factory = Factory())
        using (var client = Admin(factory))
        {
            var state = await Read(client);
            Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"concurrency":2}}""")).StatusCode);
            var first = Snapshot(factory); Assert.True(first.ConfigurationReady);
            foreach (var version in new[] { first.TextVersion, first.UkTtsVersion, first.UsTtsVersion }) Assert.True(Guid.TryParseExact(version, "N", out _));
            foreach (var patch in new[] { """{"reviewAssistant":{"model":"review-only"}}""", """{"enrichment":{"fieldsEnabled":true,"audioEnabled":true,"failureBudget":7,"scanIntervalSeconds":12,"concurrency":3}}""" })
            {
                state = await Read(client); Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), patch)).StatusCode);
                Assert.Equal(first.TextVersion, Snapshot(factory).TextVersion); Assert.Equal(first.UkTtsVersion, Snapshot(factory).UkTtsVersion); Assert.Equal(first.UsTtsVersion, Snapshot(factory).UsTtsVersion);
            }
            state = await Read(client); Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"text":{"model":"new-plan"}}}""")).StatusCode);
            var second = Snapshot(factory); Assert.NotEqual(first.TextVersion, second.TextVersion); Assert.Equal(first.UkTtsVersion, second.UkTtsVersion);
            state = await Read(client); Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"tts":{"uk":{"voice":"uk-only"}}}}""")).StatusCode);
            var third = Snapshot(factory); Assert.NotEqual(second.UkTtsVersion, third.UkTtsVersion); Assert.Equal(second.UsTtsVersion, third.UsTtsVersion);
            state = await Read(client); Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"tts":{"apiKey":{"action":"replace","value":"synthetic-enrichment-key-marker"}}}}""")).StatusCode);
            var fourth = Snapshot(factory); Assert.NotEqual(third.UkTtsVersion, fourth.UkTtsVersion); Assert.NotEqual(third.UsTtsVersion, fourth.UsTtsVersion); Assert.Equal(third.TextVersion, fourth.TextVersion);
            state = await Read(client); Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"tts":{"apiKey":{"action":"keep"}}}}""")).StatusCode);
            Assert.Equal(fourth.UkTtsVersion, Snapshot(factory).UkTtsVersion);
            text = fourth.TextVersion; uk = fourth.UkTtsVersion; us = fourth.UsTtsVersion; etag = Revision(await Read(client));
        }
        using var restarted = Factory(); using var restartedClient = Admin(restarted);
        var restored = SnapshotAfterStart(restarted, restartedClient);
        Assert.Equal(text, restored.TextVersion); Assert.Equal(uk, restored.UkTtsVersion); Assert.Equal(us, restored.UsTtsVersion);
        Assert.NotEqual(etag, Revision(await Read(restartedClient)));
    }
    private static EnrichmentConfigurationSnapshot SnapshotAfterStart(VocabularyWebApplicationFactory factory, HttpClient _) => Snapshot(factory);

    [Theory]
    [InlineData(null, 401)]
    [InlineData("user", 403)]
    [InlineData("submission", 403)]
    [InlineData("admin", 200)]
    public async Task BothMethods_RequireAdmin(string? role, int status)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(role));
        using var get = await client.GetAsync(Url, Ct); Assert.Equal((HttpStatusCode)status, get.StatusCode);
        var revision = (await factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().ReadAsync(Ct)).Revision;
        Assert.Equal((HttpStatusCode)status, (await Put(client, revision, """{"enrichment":{"audioEnabled":false}}""")).StatusCode);
    }
    [Fact]
    public async Task CookieCsrfAndBearerPriority_ArePreserved()
    {
        using var factory = Factory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie());
        var state = await Read(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, Revision(state), """{"enrichment":{"audioEnabled":false}}""")).StatusCode);
        AdminTestAntiforgery.Attach(client, factory);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"audioEnabled":false}}""")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-synthetic-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin")); client.DefaultRequestHeaders.Remove("X-SignaCore-CSRF");
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)), """{"enrichment":{"audioEnabled":false}}""")).StatusCode);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enrichment\":{\"skipPreparation\":true}}")]
    [InlineData("{\"enrichment\":{\"tts\":{\"provider\":\"Unknown\"}}}")]
    [InlineData("{\"enrichment\":{\"concurrency\":0}}")]
    [InlineData("{\"enrichment\":{\"concurrency\":1,\"concurrency\":2}}")]
    [InlineData("{\"reviewAssistant\":{\"apiKey\":\"synthetic-enrichment-key-marker\"}}")]
    [InlineData("{\"reviewAssistant\":{\"apiKey\":{\"action\":\"keep\",\"value\":\"synthetic-enrichment-key-marker\"}}}")]
    [InlineData("{\"enrichment\":{\"text\":{\"endpoint\":\"https://user:password@example.test\"}}}")]
    public async Task InvalidInputs_DoNotWriteOrPublish(string json)
    {
        using var factory = Factory(); using var client = Admin(factory); var current = Snapshot(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, Revision(await Read(client)), json)).StatusCode);
        Assert.Same(current, Snapshot(factory)); Assert.False(File.Exists(FilePath));
    }
    [Fact]
    public async Task RevisionBoundsAndCrossModuleConcurrency_KeepOneCommit()
    {
        using var factory = Factory(); using var client = Admin(factory); var state = await Read(client);
        Assert.Equal((HttpStatusCode)428, (await Put(client, null, """{"enrichment":{"concurrency":2}}""")).StatusCode);
        foreach (var revision in new[] { "*", "W/" + Revision(state), Revision(state) + ", " + Revision(state) })
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, revision, """{"enrichment":{"concurrency":2}}""")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Put(client, Revision(state), new string(' ', 17000))).StatusCode);
        using var other = new HttpRequestMessage(HttpMethod.Put, "/admin/system/settings/rate-limits") { Content = JsonContent.Create(new { publicApiEnabled = true }) };
        other.Headers.TryAddWithoutValidation("If-Match", Revision(state));
        var results = await Task.WhenAll(Put(client, Revision(state), """{"enrichment":{"concurrency":2}}"""), client.SendAsync(other, Ct));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentOverrides_AreReadOnlyAndNeverCopied(bool commandLine)
    {
        using var baseFactory = Factory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        { if (commandLine) config.AddCommandLine(["--Enrichment:Text:ApiKey=" + Secret, "--Enrichment:Tts:Uk:Voice=locked"]); else config.Add(new OverrideSource()); }));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var state = await Read(client); Assert.False(state["fields"]!["enrichment.text.apiKey"]!["editable"]!.GetValue<bool>());
        Assert.Equal(commandLine ? "commandLine" : "environment", state["fields"]!["enrichment.text.apiKey"]!["source"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await Put(client, Revision(state), """{"enrichment":{"text":{"apiKey":{"action":"clear"}}}}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"enrichment":{"text":{"apiKey":{"action":"keep"}},"concurrency":2}}""")).StatusCode);
        Assert.DoesNotContain(Secret, File.ReadAllText(FilePath)); Assert.True(SnapshotFromServices(factory.Services).ConfigurationReady);
    }
    private static EnrichmentConfigurationSnapshot SnapshotFromServices(IServiceProvider services) => services.GetRequiredService<EnrichmentRuntimeConfiguration>().Current;
    private sealed class OverrideSource : IConfigurationSource { public IConfigurationProvider Build(IConfigurationBuilder builder) => new OverrideProvider(); }
    private sealed class OverrideProvider : EnvironmentVariablesConfigurationProvider
    { public override void Load() => Data = new Dictionary<string, string?> { ["Enrichment:Text:ApiKey"] = Secret, ["Enrichment:Tts:Uk:Voice"] = "locked" }; }

    [Theory]
    [InlineData("write")]
    [InlineData("replace")]
    [InlineData("beforeCommit")]
    [InlineData("afterCommit")]
    public async Task AtomicFailureAndCancellation_KeepFileAndRuntimeInSameGeneration(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FaultStore(_root, phase, cancellation);
        var configuration = new ConfigurationBuilder().Build();
        using var configurationLifetime = configuration as IDisposable;
        var coordinator = new PersistentConfigurationCoordinator(configuration, store);
        var runtime = new EnrichmentRuntimeConfiguration(configuration, new(_root)); var initial = runtime.Current;
        var fields = new Dictionary<string, JsonNode?> { ["Enrichment:Text:ApiKey"] = JsonValue.Create(Secret) };
        var revision = (await coordinator.ReadAsync(Ct)).Revision;
        if (phase == "beforeCommit")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.SaveAsync(revision, fields, runtime.PrepareAsync, cancellation.Token));
        else
        {
            var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, fields, runtime.PrepareAsync, cancellation.Token);
            Assert.Equal(phase == "afterCommit" ? ConfigurationSaveStatus.Success : ConfigurationSaveStatus.StorageFailure, result.Status);
        }
        if (phase == "afterCommit")
        {
            Assert.NotSame(initial, runtime.Current); Assert.True(runtime.Current.ConfigurationReady);
            var restartConfig = new ConfigurationBuilder().AddJsonFile(FilePath).Build(); using var restartLifetime = restartConfig as IDisposable; var restart = new EnrichmentRuntimeConfiguration(restartConfig, new(_root));
            Assert.Equal(runtime.Current.TextVersion, restart.Current.TextVersion);
        }
        else { Assert.Same(initial, runtime.Current); Assert.False(File.Exists(FilePath)); }
    }
    private sealed class FaultStore(string root, string phase, CancellationTokenSource cancellation) : PersistentConfigurationFileStore(root)
    {
        protected override async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken token)
        {
            if (phase == "write") throw new IOException(Secret);
            await base.WriteTemporaryAsync(path, bytes, token); if (phase == "beforeCommit") cancellation.Cancel();
        }
        protected override void Replace(string path, bool overwrite)
        { if (phase == "replace") throw new IOException(Secret); base.Replace(path, overwrite); if (phase == "afterCommit") cancellation.Cancel(); }
    }

    [Fact]
    public async Task ReviewOperations_UsePublishedSnapshot_AndKeepInFlightInputs()
    {
        using var baseFactory = Factory(); var handler = new ReviewHandler();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient(OpenAiCompatibleReviewAssistant.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var state = await Read(client);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(state), """{"reviewAssistant":{"endpoint":"https://review.example.test/v1","model":"first-model","apiKey":{"action":"replace","value":"synthetic-enrichment-key-marker"}}}""")).StatusCode);
        var assistant = factory.Services.GetRequiredService<IMeaningSubmissionReviewAssistant>(); Assert.True(assistant.IsEnabled);
        var first = assistant.ReviewAsync(new MeaningSubmissionModel { Word = "cat", Meaning = "fixture" }, Ct);
        await handler.Started.Task.WaitAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)), """{"reviewAssistant":{"model":"next-model","apiKey":{"action":"replace","value":"synthetic-next-key"}}}""")).StatusCode);
        handler.Release.TrySetResult(); await first;
        await assistant.ReviewAsync(new MeaningSubmissionModel { Word = "dog", Meaning = "fixture" }, Ct);
        Assert.Equal(new[] { "first-model", "next-model" }, handler.Models); Assert.Equal(new[] { Secret, "synthetic-next-key" }, handler.Keys);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)), """{"reviewAssistant":{"endpoint":"","apiKey":{"action":"clear"}}}""")).StatusCode);
        Assert.False(assistant.IsEnabled);
        await Assert.ThrowsAsync<ReviewAssistantUnavailableException>(() => assistant.ReviewAsync(new MeaningSubmissionModel(), Ct)); Assert.Equal(2, handler.Models.Count);
    }
    private sealed class ReviewHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Models { get; } = []; public List<string> Keys { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; Models.Add(body["model"]!.GetValue<string>()); Keys.Add(request.Headers.Authorization!.Parameter!);
            if (Models.Count == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "{\"verdict\":\"accept\",\"meaning\":\"fixture\"}" } } } }) };
        }
    }
    [Fact]
    public async Task EnrichmentConsumer_RepairWakesAndCapturedOperationKeepsItsInputs()
    {
        using var factory = Factory(); using var client = Admin(factory);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)),
            """{"enrichment":{"audioEnabled":true,"text":{"endpoint":"https://planner.example.test/v1","model":"old-plan","apiKey":{"action":"replace","value":"synthetic-enrichment-key-marker"}}}}""")).StatusCode);
        var runtime = factory.Services.GetRequiredService<EnrichmentRuntimeConfiguration>(); var old = runtime.Current;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        static async Task<(string Model, string Key)> Consume(EnrichmentConfigurationSnapshot snapshot, Task release)
        { await release; return (snapshot.Enrichment.Text.Model, snapshot.Enrichment.Text.ApiKey); }
        var operation = Consume(old, release.Task); var wake = runtime.WaitForChangeAsync(old, Ct);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)),
            """{"enrichment":{"text":{"model":"new-plan","apiKey":{"action":"replace","value":"synthetic-next-key"}}}}""")).StatusCode);
        var updated = await wake; release.TrySetResult();
        Assert.Equal(("old-plan", Secret), await operation);
        Assert.Equal(("new-plan", "synthetic-next-key"), await Consume(updated, Task.CompletedTask));
        Assert.NotEqual(old.TextVersion, updated.TextVersion); Assert.Equal(old.UkTtsVersion, updated.UkTtsVersion);
    }

    [Fact]
    public async Task EnabledStartup_InitializesVersions_AndOverrideChangesOnlyAffectedVersion()
    {
        string? text; string? uk; string? us;
        using (var factory = new VocabularyWebApplicationFactory("Testing", true, contentRootPath: _root,
            extraConfiguration: new Dictionary<string, string?> { ["Enrichment:AudioEnabled"] = "true" }))
        using (var client = Admin(factory))
        {
            var current = Snapshot(factory); Assert.True(current.ConfigurationReady); Assert.True(File.Exists(FilePath));
            text = current.TextVersion; uk = current.UkTtsVersion; us = current.UsTtsVersion;
        }
        var configuration = new ConfigurationBuilder().AddJsonFile(FilePath)
            .AddCommandLine(["--Enrichment:Text:ApiKey=" + Secret]).Build();
        using var lifetime = configuration as IDisposable;
        var runtime = new EnrichmentRuntimeConfiguration(configuration, new(_root));
        Assert.Null(runtime.Current.TextVersion); Assert.Equal(uk, runtime.Current.UkTtsVersion); Assert.Equal(us, runtime.Current.UsTtsVersion);
        var coordinator = new PersistentConfigurationCoordinator(configuration, new(_root));
        await runtime.InitializeAsync(coordinator, Ct);
        Assert.True(runtime.Current.ConfigurationReady); Assert.NotEqual(text, runtime.Current.TextVersion);
        Assert.Equal(uk, runtime.Current.UkTtsVersion); Assert.Equal(us, runtime.Current.UsTtsVersion);
        Assert.DoesNotContain(Secret, File.ReadAllText(FilePath));
        var restartedConfiguration = new ConfigurationBuilder().AddJsonFile(FilePath)
            .AddCommandLine(["--Enrichment:Text:ApiKey=" + Secret]).Build();
        using var restartedLifetime = restartedConfiguration as IDisposable;
        Assert.Equal(runtime.Current.TextVersion, new EnrichmentRuntimeConfiguration(restartedConfiguration, new(_root)).Current.TextVersion);
    }
    [Fact]
    public async Task PrivateMetadata_DeploymentValuesCannotImpersonatePersistedVersions()
    {
        string oldVersion; Dictionary<string, string?> privateValues;
        using (var factory = Factory())
        using (var client = Admin(factory))
        {
            Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)), """{"enrichment":{"audioEnabled":true}}""")).StatusCode);
            oldVersion = Snapshot(factory).TextVersion!;
            var persisted = new ConfigurationBuilder().AddJsonFile(FilePath).Build(); using var lifetime = persisted as IDisposable;
            privateValues = persisted.AsEnumerable().Where(x => x.Key.StartsWith("EnrichmentConfigurationVersions", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => x.Value);
        }
        File.Delete(FilePath);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Enrichment:AudioEnabled"] = "true" })
            .AddInMemoryCollection(privateValues).Build(); using var configLifetime = configuration as IDisposable;
        var runtime = new EnrichmentRuntimeConfiguration(configuration, new(_root)); Assert.False(runtime.Current.ConfigurationReady);
        await runtime.InitializeAsync(new PersistentConfigurationCoordinator(configuration, new(_root)), Ct);
        Assert.True(runtime.Current.ConfigurationReady); Assert.NotEqual(oldVersion, runtime.Current.TextVersion);
    }

    [Fact]
    public async Task StartupStorageFailure_AndUnreadyVersions_CannotEnableConsumers()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Enrichment:AudioEnabled"] = "true", ["Enrichment:Text:Endpoint"] = "https://example.test/v1", ["Enrichment:Text:Model"] = "fixture", ["Enrichment:Text:ApiKey"] = Secret }).Build();
        using var lifetime = configuration as IDisposable; using var cancellation = new CancellationTokenSource();
        var runtime = new EnrichmentRuntimeConfiguration(configuration, new(_root));
        await runtime.InitializeAsync(new PersistentConfigurationCoordinator(configuration, new FaultStore(_root, "replace", cancellation)), Ct);
        Assert.False(runtime.Current.ConfigurationReady); Assert.False(runtime.Current.CanPrepare); Assert.False(File.Exists(FilePath));
    }
    [Fact]
    public async Task ClearKeepAndDamagedFile_DoNotPublishOrLoseVersions()
    {
        using var factory = Factory(); using var client = Admin(factory);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)),
            """{"enrichment":{"tts":{"apiKey":{"action":"replace","value":"synthetic-enrichment-key-marker"},"uk":{"speed":1.0}}}}""")).StatusCode);
        var first = Snapshot(factory);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)), """{"enrichment":{"tts":{"uk":{"speed":1}}}}""")).StatusCode);
        Assert.Equal(first.UkTtsVersion, Snapshot(factory).UkTtsVersion);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)), """{"enrichment":{"tts":{"apiKey":{"action":"clear"}}}}""")).StatusCode);
        var cleared = Snapshot(factory); Assert.NotEqual(first.UkTtsVersion, cleared.UkTtsVersion); Assert.Equal("", cleared.Enrichment.Tts.ApiKey);
        var bytes = File.ReadAllBytes(FilePath); File.WriteAllText(FilePath, "{broken");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Put(client, Revision(await Read(client)), """{"enrichment":{"concurrency":2}}""")).StatusCode);
        Assert.Same(cleared, Snapshot(factory)); Assert.Equal("{broken", File.ReadAllText(FilePath));
        File.WriteAllBytes(FilePath, bytes);
    }
    [Fact]
    public async Task SettingsLogsAndTelemetry_DoNotExposeCredentialsOrPrivateMetadata()
    {
        var previous = Console.Out; using var console = new StringWriter(); Console.SetOut(TextWriter.Synchronized(console));
        try
        {
            var spans = new CapturedSpans();
            using var baseFactory = Factory(new()
            { ["Telemetry:Otlp:Traces:Enabled"] = "true", ["Telemetry:Otlp:Traces:Endpoint"] = "http://127.0.0.1:9/", ["Telemetry:Otlp:Traces:Protocol"] = "HttpProtobuf" });
            using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.ConfigureOpenTelemetryTracerProvider((_, tracing) => tracing.AddProcessor(spans))));
            using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
            Assert.Equal(HttpStatusCode.OK, (await Put(client, Revision(await Read(client)), """{"reviewAssistant":{"apiKey":{"action":"replace","value":"synthetic-enrichment-key-marker"}}}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, Revision(await Read(client)), """{"reviewAssistant":{"apiKey":{"action":"keep","value":"synthetic-enrichment-key-marker"}}}""")).StatusCode);
            var relevant = spans.Activities.Where(a => a.TagObjects.Any(t => t.Key == "url.path" && t.Value?.ToString() == Url)).ToArray(); Assert.NotEmpty(relevant);
            foreach (var activity in relevant)
            {
                Assert.DoesNotContain(Secret, activity.DisplayName);
                foreach (var tag in activity.TagObjects.Concat(activity.Events.SelectMany(e => e.Tags))) Assert.DoesNotContain(Secret, tag.Value?.ToString() ?? "");
            }
            Assert.DoesNotContain(Secret, console.ToString()); Assert.DoesNotContain("EnrichmentConfigurationVersions", console.ToString());
        }
        finally { Console.SetOut(previous); }
    }
    private sealed class CapturedSpans : OpenTelemetry.BaseProcessor<Activity>
    {
        public ConcurrentBag<Activity> Activities { get; } = [];
        public override void OnEnd(Activity activity) => Activities.Add(activity);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
