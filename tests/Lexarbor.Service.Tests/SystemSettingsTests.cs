using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class SystemSettingsTests : IDisposable
{
    private const string PathUrl = "/admin/system/settings/rate-limits";
    private readonly string _root = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"settings-{Guid.NewGuid():N}");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string FilePath => Path.Combine(_root, "data", "appsettings.json");

    public SystemSettingsTests()
    {
        // Keep the real image's SQL suppression in this isolated host. The suite
        // has console-capture tests running concurrently with these factories.
        File.WriteAllText(Path.Combine(_root, "appsettings.json"),
            """{"Logging":{"LogLevel":{"Default":"Information","Microsoft.AspNetCore":"Warning","Microsoft.EntityFrameworkCore.Database.Command":"Warning"}}}""");
    }


    private VocabularyWebApplicationFactory Factory(Dictionary<string, string?>? config = null) => new("Testing", true,
        extraConfiguration: config, contentRootPath: _root);
    private static HttpClient Admin(VocabularyWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        return client;
    }
    private static async Task<SystemSettingsEndpoints.RateLimitsResponse> Read(HttpClient client)
    {
        var response = await client.GetAsync(PathUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var result = (await response.Content.ReadFromJsonAsync<SystemSettingsEndpoints.RateLimitsResponse>(Ct))!;
        Assert.Equal(result.Revision, response.Headers.ETag?.ToString());
        return result;
    }
    private static Task<HttpResponseMessage> Put(HttpClient client, string? revision, string json = "{\"publicApiEnabled\":true}")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, PathUrl) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        return client.SendAsync(request, Ct);
    }
    private static Task<HttpResponseMessage> Request(HttpClient client, string path, string method = "GET")
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.81");
        if (method == "POST") request.Content = JsonContent.Create(new { items = new[] { new { wordId = "missing", bookId = "missing" } } });
        return client.SendAsync(request, Ct);
    }

    [Theory]
    [InlineData("publicApiEnabled", "/api/unknown", "GET", "/admin/auth/start")]
    [InlineData("publicApiEnabled", "/api/vocabulary-books/all", "GET", "/admin/auth/start")]
    [InlineData("publicApiEnabled", "/api/vocabulary/questions", "POST", "/admin/auth/start")]
    [InlineData("publicApiEnabled", "/api/vocabulary/missing/audio?bookId=missing", "GET", "/admin/auth/start")]
    [InlineData("adminLoginEnabled", "/ADMIN/AUTH/START/", "GET", "/api/unknown")]
    public async Task LiveSwitch_UsesConfiguredQuotaAndRetainsBucketAcrossDisable(string field, string path, string method, string other)
    {
        using var factory = Factory(new()
        {
            ["RateLimits:AdminLogin:PermitLimit"] = "1",
            ["RateLimits:PublicApi:PermitLimit"] = "1",
            ["RateLimits:AdminLogin:WindowSeconds"] = "600",
            ["RateLimits:PublicApi:WindowSeconds"] = "600"
        });
        using var client = Admin(factory);
        Assert.Equal(FilePath, factory.Services.GetRequiredService<PersistentConfigurationFileStore>().FilePath);
        var initial = await Read(client);
        Assert.False(initial.AdminLogin.Enabled);
        Assert.False(initial.PublicApi.Enabled);
        Assert.False(File.Exists(FilePath));
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Request(client, path, method)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, initial.Revision, $"{{\"{field}\":true}}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Request(client, path, method)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Request(client, path, method)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Request(client, other)).StatusCode);
        foreach (var unmetered in new[] { PathUrl, "/admin/auth/callback", "/admin/auth/logout/return", "/health/live", "/health/ready", "/admin/system/version" })
        {
            var response = await Request(client, unmetered);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.False(response.Headers.Contains("Retry-After"));
        }
        var enabled = await Read(client);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, enabled.Revision, $"{{\"{field}\":false}}")).StatusCode);
        var disabledResponse = await Request(client, path, method);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, disabledResponse.StatusCode);
        Assert.False(disabledResponse.Headers.Contains("Retry-After"));
        var disabled = await Read(client);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, disabled.Revision, $"{{\"{field}\":true}}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Request(client, path, method)).StatusCode);
        Assert.Equal("file", (await Read(client)).PublicApi.Source == "file" ? "file" : (await Read(client)).AdminLogin.Source);
    }

    [Theory]
    [InlineData("AdminLogin", "adminLoginEnabled")]
    [InlineData("PublicApi", "publicApiEnabled")]
    public async Task InvalidDisabledNumbers_AreDisplayedButCannotBeEnabled(string name, string field)
    {
        using var factory = Factory(new() { [$"RateLimits:{name}:PermitLimit"] = "-1", [$"RateLimits:{name}:WindowSeconds"] = "601" });
        using var client = Admin(factory);
        var state = await Read(client);
        var policy = name == "AdminLogin" ? state.AdminLogin : state.PublicApi;
        Assert.Equal(-1, policy.PermitLimit);
        Assert.Equal(601, policy.WindowSeconds);
        var response = await Put(client, state.Revision, $"{{\"{field}\":true}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(state, await Read(client));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"publicApiEnabled\":\"true\"}")]
    [InlineData("{\"publicApiEnabled\":null}")]
    [InlineData("{\"publicApiEnabled\":true,\"windowSeconds\":60}")]
    [InlineData("{\"publicApiEnabled\":true,\"publicApiEnabled\":false}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task InvalidRequest_DoesNotWrite(string json)
    {
        using var factory = Factory(); using var client = Admin(factory);
        var state = await Read(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, state.Revision, json)).StatusCode);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(state, await Read(client));
    }

    [Fact]
    public async Task VersionConcurrentSaveAndStorageFailure_KeepRuntimeConsistent()
    {
        using var factory = Factory(); using var client = Admin(factory);
        var initial = await Read(client);
        Assert.Equal((HttpStatusCode)428, (await Put(client, null)).StatusCode);
        foreach (var value in new[] { "*", "W/" + initial.Revision, "bad", initial.Revision + ", " + initial.Revision })
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, value)).StatusCode);
        var answers = await Task.WhenAll(Put(client, initial.Revision), Put(client, initial.Revision, "{\"adminLoginEnabled\":true}"));
        Assert.Single(answers, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(answers, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        var state = await Read(client);
        var bytes = File.ReadAllBytes(FilePath);
        // The store refuses an explicitly read-only file even when the test runs as root.
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        try
        {
            var result = await Put(client, state.Revision, "{\"publicApiEnabled\":false,\"adminLoginEnabled\":false}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            Assert.Equal(bytes, File.ReadAllBytes(FilePath));
            Assert.Equal(state, await Read(client));
        }
        finally { File.SetAttributes(FilePath, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("commandLine")]
    public async Task DeploymentOverride_IsReadOnlyAndOtherSwitchRemainsEditable(string source)
    {
        using var baseFactory = Factory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            // A synthetic provider instance avoids process-global environment mutations.
            if (source == "environment") config.Add(new SyntheticEnvironmentSource());
            else config.AddCommandLine(["--RateLimits:AdminLogin:Enabled=true"]);
        }));
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var state = await Read(client);
        Assert.True(state.AdminLogin.Enabled); Assert.False(state.AdminLogin.Editable);
        Assert.Equal(source, state.AdminLogin.Source);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(client, state.Revision, "{\"adminLoginEnabled\":false}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision)).StatusCode);
        Assert.True((await Read(client)).AdminLogin.Enabled);
    }
    private sealed class SyntheticEnvironmentSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder _) => new SyntheticEnvironmentProvider();
    }
    private sealed class SyntheticEnvironmentProvider : Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider
    {
        public override void Load() => Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["RateLimits:AdminLogin:Enabled"] = "true" };
    }

    [Fact]
    public async Task Save_PersistsAndReloadsWithoutSerializingUnrelatedEnvironmentValues()
    {
        using (var factory = Factory())
        using (var client = Admin(factory))
        {
            var state = await Read(client);
            Assert.Equal(HttpStatusCode.OK, (await Put(client, state.Revision, "{\"adminLoginEnabled\":true,\"publicApiEnabled\":true}")).StatusCode);
            Assert.DoesNotContain("vocabulary-client-secret", File.ReadAllText(FilePath));
        }
        using var restarted = Factory(); using var restartedClient = Admin(restarted);
        var restored = await Read(restartedClient);
        Assert.True(restored.AdminLogin.Enabled); Assert.True(restored.PublicApi.Enabled);
        Assert.Equal("file", restored.AdminLogin.Source);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("user", 403)]
    [InlineData("admin", 200)]
    public async Task AuthenticationAndSecurityHeaders_ApplyToBothMethods(string? role, int expected)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(role));
        foreach (var response in new[] { await client.GetAsync(PathUrl, Ct), await Put(client, (await factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().ReadAsync(Ct)).Revision) })
        {
            Assert.Equal((HttpStatusCode)expected, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    [Fact]
    public async Task CookieRequiresCsrf_AndInvalidBearerDoesNotFallbackToCookie()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie());
        var initial = await Read(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, initial.Revision)).StatusCode);
        Assert.False(File.Exists(FilePath));
        AdminTestAntiforgery.Attach(client, factory);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, initial.Revision)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-synthetic-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PathUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, (await factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().ReadAsync(Ct)).Revision)).StatusCode);
    }

    [Theory]
    [InlineData("/settings", "/#/settings")]
    [InlineData("/#/settings", "/#/settings")]
    [InlineData("https://example.test/settings", null)]
    [InlineData("/%73ettings", null)]
    [InlineData("/settings?next=x", null)]
    [InlineData("//settings", null)]
    [InlineData("/settings/", null)]
    public void SettingsReturnTarget_IsExactAndLocal(string input, string? expected) => Assert.Equal(expected, AdminLoginReturnTarget.Normalize(input));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
