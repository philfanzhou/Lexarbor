using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Host;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Trace;

namespace Lexarbor.Service.Tests;

public sealed class AudioStorageSettingsTests : IDisposable
{
    private const string Url = "/admin/system/settings/audio-storage";
    private const string Access = "synthetic-settings-access-marker";
    private const string Secret = "synthetic-settings-secret-marker";
    private readonly string _root = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"audio-settings-{Guid.NewGuid():N}");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string FilePath => Path.Combine(_root, "data", "appsettings.json");
    public AudioStorageSettingsTests() => File.WriteAllText(Path.Combine(_root, "appsettings.json"),
        """{"Logging":{"LogLevel":{"Default":"Information","Microsoft.AspNetCore":"Warning","Microsoft.EntityFrameworkCore.Database.Command":"Warning"}},"Unknown":{"retain":42},"RateLimits":{"PublicApi":{"Enabled":false}}}""");
    private VocabularyWebApplicationFactory Factory(Dictionary<string, string?>? config = null) => new("Testing", true,
        extraConfiguration: config, contentRootPath: _root);
    private static HttpClient Admin(VocabularyWebApplicationFactory factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin")); return client;
    }
    private static async Task<AudioStorageSettingsEndpoints.AudioStorageResponse> Read(HttpClient client)
    {
        using var response = await client.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Secure(response);
        var text = await response.Content.ReadAsStringAsync(Ct); NoSecrets(text);
        var result = JsonSerializer.Deserialize<AudioStorageSettingsEndpoints.AudioStorageResponse>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(result.Revision, response.Headers.ETag?.ToString()); return result;
    }
    private static async Task<HttpResponseMessage> Put(HttpClient client, string? revision, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        var response = await client.SendAsync(request, Ct); Secure(response); NoSecrets(await response.Content.ReadAsStringAsync(Ct)); return response;
    }
    private static void Secure(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
    private static void NoSecrets(string text)
    {
        Assert.DoesNotContain(Access, text); Assert.DoesNotContain(Secret, text);
    }
    private static string Complete(bool enabled = true) => JsonSerializer.Serialize(new
    {
        enabled,
        internalEndpoint = "http://127.0.0.1:9/",
        publicEndpoint = "https://media.example.test/s3/",
        region = "us-east-1",
        bucket = "settings-test-bucket",
        keyPrefix = "audio/",
        accessKey = new { action = "replace", value = Access },
        secretKey = new { action = "replace", value = Secret }
    });

    [Fact]
    public async Task MissingFile_OfflineValidSaveKeepReplaceClearAndRestart_PreserveCompleteCandidate()
    {
        using (var factory = Factory())
        using (var client = Admin(factory))
        {
            var initial = await Read(client); Assert.False(initial.Enabled); Assert.False(initial.HasAccessKey); Assert.False(File.Exists(FilePath));
            Assert.Equal(8, initial.Fields.Count); Assert.All(initial.Fields.Values, field => Assert.True(field.Editable));
            Assert.Equal(HttpStatusCode.OK, (await Put(client, initial.Revision, Complete())).StatusCode);
            var saved = await Read(client); Assert.True(saved.Enabled); Assert.True(saved.HasAccessKey); Assert.True(saved.HasSecretKey);
            Assert.All(saved.Fields.Values, field => Assert.Equal("file", field.Source));
            Assert.DoesNotContain(Access, saved.Revision); Assert.DoesNotContain(Secret, saved.Revision);
            using var reference = factory.Services.GetRequiredService<AudioRuntimeConfiguration>().Acquire();
            Assert.Equal(saved.Bucket, reference.Snapshot.Bucket);
            File.Delete(FilePath);
            Assert.Equal(HttpStatusCode.OK, (await Put(client, saved.Revision, """{"publicEndpoint":"https://new.example.test/media/","accessKey":{"action":"keep"}}""")).StatusCode);
            var kept = await Read(client); Assert.True(kept.HasAccessKey); Assert.True(kept.HasSecretKey);
            using var file = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            Assert.Equal(Access, file.RootElement.GetProperty("Audio").GetProperty("AccessKey").GetString());
            Assert.Equal(Secret, file.RootElement.GetProperty("Audio").GetProperty("SecretKey").GetString());
            Assert.Equal(42, file.RootElement.GetProperty("Unknown").GetProperty("retain").GetInt32());
            Assert.False(file.RootElement.GetProperty("RateLimits").GetProperty("PublicApi").GetProperty("Enabled").GetBoolean());
            Assert.DoesNotContain("vocabulary-client-secret", File.ReadAllText(FilePath));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, kept.Revision, """{"secretKey":{"action":"clear"}}""")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Put(client, kept.Revision, """{"enabled":false,"secretKey":{"action":"clear"}}""")).StatusCode);
            var cleared = await Read(client); Assert.False(cleared.Enabled); Assert.False(cleared.HasSecretKey); Assert.True(cleared.HasAccessKey);
            Assert.Equal(HttpStatusCode.OK, (await Put(client, cleared.Revision, """{"secretKey":{"action":"replace","value":"synthetic-settings-secret-marker"},"enabled":true}""")).StatusCode);
        }
        using var restarted = Factory(); using var restartClient = Admin(restarted);
        var restored = await Read(restartClient); Assert.True(restored.Enabled); Assert.True(restored.HasSecretKey);
        Assert.Equal("https://new.example.test/media/", restored.PublicEndpoint);
    }

    [Theory]
    [InlineData("accessKey")]
    [InlineData("secretKey")]
    public async Task IndependentCredentialClear_RequiresDisabledOrValidCompleteCandidate(string field)
    {
        using var factory = Factory(); using var client = Admin(factory); var initial = await Read(client);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, initial.Revision, Complete())).StatusCode); var state = await Read(client);
        var patch = JsonSerializer.Serialize(new Dictionary<string, object> { [field] = new { action = "clear" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, state.Revision, patch)).StatusCode);
        patch = JsonSerializer.Serialize(new Dictionary<string, object> { [field] = new { action = "clear" }, ["enabled"] = false });
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, patch)).StatusCode);
        var saved = await Read(client); Assert.False(saved.Enabled);
        Assert.Equal(field != "accessKey", saved.HasAccessKey); Assert.Equal(field != "secretKey", saved.HasSecretKey);
    }

    [Theory]
    [InlineData("PlaybackTtlSeconds", "1")]
    [InlineData("MaxConcurrentUploads", "0")]
    [InlineData("RecoveryBatchSize", "0")]
    [InlineData("OperationTimeoutSeconds", "999")]
    public async Task InvalidDisabledAdvancedConfiguration_CannotBeEnabledOnline(string name, string value)
    {
        using var factory = Factory(new() { ["Audio:" + name] = value }); using var client = Admin(factory); var initial = await Read(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, initial.Revision, Complete())).StatusCode);
        Assert.False(File.Exists(FilePath)); Assert.False((await Read(client)).Enabled);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"enabled\":\"true\"}")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"bucket\":12}")]
    [InlineData("{\"enabled\":true,\"enabled\":false}")]
    [InlineData("{\"PlaybackTtlSeconds\":60}")]
    [InlineData("{\"maxConcurrentUploads\":8}")]
    [InlineData("{\"filePath\":\"synthetic-settings-secret-marker\"}")]
    [InlineData("{\"accessKey\":\"synthetic-settings-access-marker\"}")]
    [InlineData("{\"secretKey\":{\"action\":\"replace\"}}")]
    [InlineData("{\"secretKey\":{\"action\":\"replace\",\"value\":\" \"}}")]
    [InlineData("{\"secretKey\":{\"action\":\"keep\",\"value\":\"synthetic-settings-secret-marker\"}}")]
    [InlineData("{\"secretKey\":{\"action\":\"clear\",\"value\":null}}")]
    [InlineData("{\"secretKey\":{\"action\":\"unknown\"}}")]
    [InlineData("{\"secretKey\":{\"action\":\"keep\",\"action\":\"clear\"}}")]
    [InlineData("{\"secretKey\":{\"action\":\"keep\",\"extra\":\"synthetic-settings-secret-marker\"}}")]
    [InlineData("{\"enabled\":true}")]
    public async Task InvalidRequests_DoNotCreateFileOrPublish(string json)
    {
        using var factory = Factory(); using var client = Admin(factory); var before = await Read(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, before.Revision, json)).StatusCode);
        Assert.False(File.Exists(FilePath)); Assert.Equal(before.Revision, (await Read(client)).Revision); Assert.False((await Read(client)).Enabled);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("user", 403)]
    [InlineData("admin", 200)]
    public async Task BothMethods_RequireAdministratorAndCarrySecurityHeaders(string? role, int expected)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(role));
        using var get = await client.GetAsync(Url, Ct); Secure(get); Assert.Equal((HttpStatusCode)expected, get.StatusCode);
        var revision = (await factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().ReadAsync(Ct)).Revision;
        Assert.Equal((HttpStatusCode)expected, (await Put(client, revision, """{"enabled":false}""")).StatusCode);
    }

    [Fact]
    public async Task CookieCsrfAndMixedCredentials_KeepBearerPriority()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie()); var state = await Read(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, state.Revision, Complete(false))).StatusCode);
        AdminTestAntiforgery.Attach(client, factory);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, Complete(false))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-synthetic-token");
        using var get = await client.GetAsync(Url, Ct); Secure(get); Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, (await factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().ReadAsync(Ct)).Revision, Complete())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin"));
        client.DefaultRequestHeaders.Remove("X-SignaCore-CSRF");
        Assert.Equal(HttpStatusCode.OK, (await Put(client, (await factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().ReadAsync(Ct)).Revision, Complete())).StatusCode);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("commandLine")]
    public async Task Overrides_AreReadOnlyAndCredentialsAreNeverPersistedFromDeployment(string source)
    {
        using var baseFactory = Factory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            if (source == "environment") config.Add(new SyntheticEnvironmentSource());
            else config.AddCommandLine(["--Audio:AccessKey=" + Access, "--Audio:SecretKey=" + Secret, "--Audio:Bucket=locked-bucket"]);
        }));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var state = await Read(client); Assert.True(state.HasAccessKey); Assert.True(state.HasSecretKey);
        foreach (var field in new[] { "accessKey", "secretKey", "bucket" })
        {
            Assert.Equal(source, state.Fields[field].Source); Assert.False(state.Fields[field].Editable); Assert.NotNull(state.Fields[field].Reason);
        }
        foreach (var json in new[] { """{"bucket":"new-bucket"}""", """{"accessKey":{"action":"clear"}}""", """{"secretKey":{"action":"replace","value":"synthetic-settings-secret-marker"}}""" })
            Assert.Equal(HttpStatusCode.Conflict, (await Put(client, state.Revision, json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, """{"enabled":false,"accessKey":{"action":"keep"}}""")).StatusCode);
        NoSecrets(File.ReadAllText(FilePath)); Assert.Equal("locked-bucket", (await Read(client)).Bucket);
    }
    private sealed class SyntheticEnvironmentSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new SyntheticEnvironmentProvider();
    }
    private sealed class SyntheticEnvironmentProvider : Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider
    {
        public override void Load() => Data = new Dictionary<string, string?> { ["Audio:AccessKey"] = Access, ["Audio:SecretKey"] = Secret, ["Audio:Bucket"] = "locked-bucket" };
    }

    [Fact]
    public async Task ExplicitKeep_WithAllFieldsOverridden_DoesNotCopyDeploymentCredentials()
    {
        using var baseFactory = Factory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddCommandLine(["--Audio:Enabled=false", "--Audio:InternalEndpoint=http://127.0.0.1:9/",
                "--Audio:PublicEndpoint=https://media.example.test/", "--Audio:Region=us-east-1", "--Audio:Bucket=locked-bucket",
                "--Audio:KeyPrefix=audio/", "--Audio:AccessKey=" + Access, "--Audio:SecretKey=" + Secret])));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var state = await Read(client); Assert.All(state.Fields.Values, field => Assert.False(field.Editable));
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, """{"secretKey":{"action":"keep"}}""")).StatusCode);
        NoSecrets(File.ReadAllText(FilePath)); Assert.True((await Read(client)).HasSecretKey);
    }

    [Fact]
    public async Task VersionValidationAndCrossModuleConcurrency_DoNotOverwriteSuccessfulSave()
    {
        using var factory = Factory(); using var client = Admin(factory); var initial = await Read(client);
        Assert.Equal((HttpStatusCode)428, (await Put(client, null, Complete())).StatusCode);
        foreach (var value in new[] { "*", "W/" + initial.Revision, "bad", initial.Revision + ", " + initial.Revision })
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, value, Complete())).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/system/settings/rate-limits") { Content = JsonContent.Create(new { publicApiEnabled = true }) };
        request.Headers.TryAddWithoutValidation("If-Match", initial.Revision);
        var answers = await Task.WhenAll(Put(client, initial.Revision, Complete()), client.SendAsync(request, Ct));
        Assert.Single(answers, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(answers, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Put(client, initial.Revision, Complete())).StatusCode);
    }

    [Theory]
    [InlineData("readonly")]
    [InlineData("damaged")]
    [InlineData("symlink")]
    [InlineData("write")]
    [InlineData("replace")]
    [InlineData("construct")]
    public async Task StorageAndCandidateFailures_KeepOldFileAndRuntime(string phase)
    {
        if (phase == "symlink" && OperatingSystem.IsWindows()) return;
        using var baseFactory = Factory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<PersistentConfigurationFileStore>(); services.AddSingleton<PersistentConfigurationFileStore>(new FaultStore(_root, phase));
            services.RemoveAll<PersistentConfigurationCoordinator>();
            services.AddSingleton(provider => new PersistentConfigurationCoordinator((IConfigurationRoot)provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<PersistentConfigurationFileStore>()));
            if (phase == "construct") { services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(new FailingFactory()); }
        }));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var initial = await Read(client);
        if (phase is "write" or "replace" or "construct")
        {
            Assert.Equal(phase == "construct" ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable, (await Put(client, initial.Revision, Complete())).StatusCode);
            Assert.False(File.Exists(FilePath)); Assert.False((await Read(client)).Enabled); return;
        }
        Assert.Equal(HttpStatusCode.OK, (await Put(client, initial.Revision, Complete())).StatusCode); var state = await Read(client);
        var old = File.ReadAllBytes(FilePath); var target = FilePath + ".real";
        if (phase == "readonly") File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        else if (phase == "damaged") File.WriteAllText(FilePath, "{damaged");
        else { File.Move(FilePath, target); File.CreateSymbolicLink(FilePath, target); }
        try
        {
            var expected = File.ReadAllBytes(FilePath);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Put(client, state.Revision, """{"enabled":false}""")).StatusCode);
            Assert.Equal(expected, File.ReadAllBytes(FilePath)); Assert.True((await Read(client)).Enabled);
        }
        finally
        {
            if (phase == "readonly") File.SetAttributes(FilePath, FileAttributes.Normal);
            else if (phase == "symlink") { File.Delete(FilePath); File.Move(target, FilePath); }
            else File.WriteAllBytes(FilePath, old);
        }
    }
    private sealed class FaultStore(string root, string phase) : PersistentConfigurationFileStore(root)
    {
        protected override Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken token)
            => phase == "write" ? throw new IOException(Secret) : base.WriteTemporaryAsync(path, bytes, token);
        protected override void Replace(string path, bool overwrite)
        {
            if (phase == "replace") throw new IOException(Secret); base.Replace(path, overwrite);
        }
    }
    private sealed class FailingFactory : IAudioObjectStoreFactory
    {
        public Lexarbor.Domain.Repositories.IAudioObjectStore Create(AudioOptions options)
        {
            if (options.Enabled) throw new InvalidOperationException(Secret);
            return new S3AudioObjectStore(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
        }
    }

    [Fact]
    public async Task ActiveOperation_DestinationChangeIs409ButRotationAndDisableSucceed()
    {
        using var factory = Factory(); using var client = Admin(factory); var state = await Read(client);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, Complete())).StatusCode); state = await Read(client);
        using var active = factory.Services.GetRequiredService<AudioRuntimeConfiguration>().Acquire(); var bytes = File.ReadAllBytes(FilePath);
        foreach (var json in new[] { """{"bucket":"changed-bucket"}""", """{"internalEndpoint":"http://127.0.0.1:10/"}""", """{"keyPrefix":"changed/"}""" })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await Put(client, state.Revision, json)).StatusCode);
            Assert.Equal(bytes, File.ReadAllBytes(FilePath)); Assert.Equal(state.Revision, (await Read(client)).Revision);
        }
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, """{"enabled":false,"publicEndpoint":"https://rotated.example.test/","secretKey":{"action":"replace","value":"synthetic-settings-secret-marker"}}""")).StatusCode);
        Assert.True(active.Snapshot.Enabled); Assert.Equal("https://media.example.test/s3/", active.Snapshot.PublicEndpoint); Assert.False((await Read(client)).Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnyUnboundAsset_RejectsDestinationThroughHttp(bool published)
    {
        using var factory = Factory(); using var client = Admin(factory); var initial = await Read(client);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, initial.Revision, Complete(false))).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.AudioAssets.Add(new()
            {
                Id = Guid.NewGuid().ToString(),
                Bucket = "settings-test-bucket",
                ObjectKey = $"audio/{Guid.NewGuid():N}.mp3",
                Sha256 = new string('a', 64),
                ByteLength = 1,
                CreatedAtUnixMs = 1,
                FirstPublishedAtUnixMs = published ? 2 : null
            });
            await db.SaveChangesAsync(Ct);
        }
        var state = await Read(client); var bytes = File.ReadAllBytes(FilePath);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(client, state.Revision, """{"bucket":"changed-bucket"}""")).StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath)); Assert.Equal(state.Revision, (await Read(client)).Revision);
    }

    [Theory]
    [InlineData(Lexarbor.Domain.Models.AudioUploadState.Reserved)]
    [InlineData(Lexarbor.Domain.Models.AudioUploadState.Transferring)]
    [InlineData(Lexarbor.Domain.Models.AudioUploadState.Succeeded)]
    [InlineData(Lexarbor.Domain.Models.AudioUploadState.Abandoned)]
    [InlineData(Lexarbor.Domain.Models.AudioUploadState.CleanupClaimed)]
    [InlineData(Lexarbor.Domain.Models.AudioUploadState.CleanupObserved)]
    public async Task UnboundUploadRecord_InEveryState_RejectsDestinationThroughHttp(Lexarbor.Domain.Models.AudioUploadState state)
    {
        using var factory = Factory(); using var client = Admin(factory); var initial = await Read(client);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, initial.Revision, Complete(false))).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var assetId = Guid.NewGuid().ToString();
            db.AudioAssets.Add(new()
            {
                Id = assetId,
                Bucket = "settings-test-bucket",
                ObjectKey = $"audio/{Guid.NewGuid():N}.mp3",
                Sha256 = new string('b', 64),
                ByteLength = 1,
                CreatedAtUnixMs = 1
            });
            var live = state is Lexarbor.Domain.Models.AudioUploadState.Reserved or Lexarbor.Domain.Models.AudioUploadState.Transferring;
            var success = state == Lexarbor.Domain.Models.AudioUploadState.Succeeded;
            var cleaning = state == Lexarbor.Domain.Models.AudioUploadState.CleanupClaimed;
            db.AudioUploadOperations.Add(new()
            {
                IdempotencyKey = Guid.NewGuid().ToString(),
                AssetId = assetId,
                WordId = "missing-word",
                State = state,
                RequestFingerprint = new string('b', 64),
                SourceRevision = new string('b', 64),
                Locale = Lexarbor.Domain.Models.AudioLocale.EnGb,
                CreatedAtUnixMs = 1,
                UpdatedAtUnixMs = 1,
                ReceiptStatusCode = live ? null : success ? 200 : 503,
                ReceiptAssetId = success ? assetId : null,
                ReceiptSourceRevision = success ? new string('b', 64) : null,
                ReceiptErrorCode = live || success ? null : "storage-unavailable",
                CompletedAtUnixMs = live ? null : 1,
                UploadLeaseOwner = live ? "probe" : null,
                UploadLeaseFence = live ? 1 : 0,
                UploadLeaseExpiresAtUnixMs = live ? 100 : null,
                CleanupLeaseOwner = cleaning ? "probe" : null,
                CleanupLeaseFence = cleaning ? 1 : 0,
                CleanupLeaseExpiresAtUnixMs = cleaning ? 100 : null,
                CleanupReturnState = cleaning ? Lexarbor.Domain.Models.AudioUploadState.Abandoned : null
            });
            await db.SaveChangesAsync(Ct);
        }
        var saved = await Read(client); var bytes = File.ReadAllBytes(FilePath);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(client, saved.Revision, """{"keyPrefix":"new/"}""")).StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath)); Assert.Equal(saved.Revision, (await Read(client)).Revision);
    }

    [Fact]
    public async Task FirstSaveWithoutTemplate_PersistsRetainedCredentialsFromEffectiveDefaults()
    {
        using var factory = Factory(new() { ["Audio:AccessKey"] = Access, ["Audio:SecretKey"] = Secret }); using var client = Admin(factory);
        var state = await Read(client); File.Delete(Path.Combine(_root, "appsettings.json"));
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, """{"accessKey":{"action":"keep"}}""")).StatusCode);
        using var json = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        Assert.Equal(Access, json.RootElement.GetProperty("Audio").GetProperty("AccessKey").GetString());
        Assert.Equal(Secret, json.RootElement.GetProperty("Audio").GetProperty("SecretKey").GetString());
        Assert.False((await Read(client)).Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientDisconnect_BeforeCommitDiscards_AfterCommitPublishes(bool afterCommit)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new DisconnectStore(_root, afterCommit, started, finished, cancellation);
        using var baseFactory = Factory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<PersistentConfigurationCoordinator>();
            services.AddSingleton(provider => new PersistentConfigurationCoordinator((IConfigurationRoot)provider.GetRequiredService<IConfiguration>(), store));
        }));
        using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var initial = await Read(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, Url) { Content = new StringContent(Complete(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", initial.Revision);
        var save = client.SendAsync(request, cancellation.Token);
        await started.Task.WaitAsync(Ct);
        if (!afterCommit) await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await save);
        await finished.Task.WaitAsync(Ct);
        // A fresh GET waits on the coordinator lock until discard/publication ends.
        var confirmed = await Read(client);
        Assert.Equal(afterCommit, confirmed.Enabled); Assert.Equal(afterCommit, File.Exists(FilePath));
        Assert.Equal(afterCommit, confirmed.HasSecretKey);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data"), "*.tmp"));
    }
    private sealed class DisconnectStore(string root, bool afterCommit, TaskCompletionSource started, TaskCompletionSource finished,
        CancellationTokenSource cancellation) : PersistentConfigurationFileStore(root)
    {
        protected override async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken token)
        {
            await base.WriteTemporaryAsync(path, bytes, token);
            if (!afterCommit)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { finished.TrySetResult(); }
            }
        }
        protected override void Replace(string path, bool overwrite)
        {
            base.Replace(path, overwrite);
            started.TrySetResult(); cancellation.Cancel(); finished.TrySetResult();
        }
    }

    [Fact]
    public async Task SettingsResponsesConsoleAndTelemetry_NeverContainStoredSubmittedOrExceptionCredentials()
    {
        var original = Console.Out; using var console = new StringWriter(); Console.SetOut(TextWriter.Synchronized(console));
        try
        {
            var spans = new CapturedSpans();
            using var baseFactory = Factory(new()
            {
                ["Telemetry:Otlp:Traces:Enabled"] = "true",
                ["Telemetry:Otlp:Traces:Protocol"] = "HttpProtobuf",
                ["Telemetry:Otlp:Traces:Endpoint"] = "http://127.0.0.1:9/"
            });
            using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.ConfigureOpenTelemetryTracerProvider((_, tracing) => tracing.AddProcessor(spans));
                services.RemoveAll<IAudioObjectStoreFactory>(); services.AddSingleton<IAudioObjectStoreFactory>(new FailingFactory());
            }));
            using var client = factory.CreateHttpsClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
            var state = await Read(client);
            Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, Complete(false))).StatusCode);
            state = await Read(client); Assert.True(state.HasSecretKey);
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, state.Revision, Complete())).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, state.Revision, """{"secretKey":{"action":"keep","value":"synthetic-settings-secret-marker"}}""")).StatusCode);
            var relevant = spans.Activities.Where(activity => activity.TagObjects.Any(tag => tag.Key == "url.path" && tag.Value?.ToString() == Url)).ToArray();
            Assert.NotEmpty(relevant);
            foreach (var activity in relevant)
            {
                NoSecrets(activity.DisplayName);
                foreach (var tag in activity.TagObjects) NoSecrets(tag.Value?.ToString() ?? "");
                foreach (var item in activity.Events) foreach (var tag in item.Tags) NoSecrets(tag.Value?.ToString() ?? "");
            }
            NoSecrets(console.ToString());
        }
        finally { Console.SetOut(original); }
    }
    private sealed class CapturedSpans : OpenTelemetry.BaseProcessor<Activity>
    {
        public ConcurrentBag<Activity> Activities { get; } = [];
        public override void OnEnd(Activity activity) => Activities.Add(activity);
    }

    [Fact]
    public async Task BoundedPayload_IsRejectedWithoutSecretEcho()
    {
        using var factory = Factory(); using var client = Admin(factory); var state = await Read(client);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Put(client, state.Revision, "{\"secretKey\":\"" + new string('x', 20000) + Secret + "\"}")).StatusCode);
        Assert.False(File.Exists(FilePath));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
