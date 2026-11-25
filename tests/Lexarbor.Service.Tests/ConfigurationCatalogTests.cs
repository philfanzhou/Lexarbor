using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The settings catalog and the startup endpoint: what the surface reports, what it refuses,
/// and that whatever it accepts is exactly what the next host starts with.
/// </summary>
public sealed class ConfigurationCatalogTests : IDisposable
{
    private const string CatalogUrl = "/admin/system/settings/catalog";
    private const string StartupUrl = "/admin/system/settings/startup";
    private const string Secret = "AdminAuthentication:OidcCode:ClientSecret";
    private const string Redirect = "AdminAuthentication:OidcCode:RedirectUri";
    // A hosted-login registration that is complete at startup, as a working deployment's is.
    private static readonly Dictionary<string, string?> Configured = new()
    {
        [Redirect] = "https://admin.example.test/admin/auth/callback"
    };
    // A complete, enabled storage candidate for the live audio module.
    private const string Storage = """
        {"enabled":true,"internalEndpoint":"http://storage.test/","publicEndpoint":"https://audio.test/",
        "accessKey":{"action":"replace","value":"synthetic-access"},"secretKey":{"action":"replace","value":"synthetic-secret"}}
        """;
    private readonly string _root = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"catalog-{Guid.NewGuid():N}");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string FilePath => Path.Combine(_root, "data", "appsettings.json");

    public ConfigurationCatalogTests()
    {
        // The image's own defaults, as a deployment's content root carries them.
        File.WriteAllText(Path.Combine(_root, "appsettings.json"),
            """{"Logging":{"LogLevel":{"Default":"Information","Microsoft.AspNetCore":"Warning","Microsoft.EntityFrameworkCore.Database.Command":"Warning"}}}""");
    }

    private VocabularyWebApplicationFactory Factory(Dictionary<string, string?>? config = null) =>
        new("Testing", true, extraConfiguration: config, contentRootPath: _root);

    // A Bearer token carrying the administrator role the given host requires.
    private static HttpClient Admin(VocabularyWebApplicationFactory factory, string role = "admin")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(role));
        return client;
    }

    private static async Task<JsonElement> Read(HttpClient client)
    {
        using var response = await client.GetAsync(CatalogUrl, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(text);
        Assert.Equal(Revision(document.RootElement), response.Headers.ETag?.ToString());
        return document.RootElement.Clone();
    }

    private static string Revision(JsonElement catalog) => catalog.GetProperty("revision").GetString()!;

    private static JsonElement Field(JsonElement catalog, string key) =>
        catalog.GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("key").GetString() == key);

    private static JsonElement Fact(JsonElement catalog, string id) =>
        catalog.GetProperty("facts").EnumerateArray().Single(fact => fact.GetProperty("id").GetString() == id);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Save(HttpClient client, string? revision, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, StartupUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        using var response = await client.SendAsync(request, Ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(Ct));
        return (response.StatusCode, document.RootElement.Clone());
    }

    // Saves with the current revision and requires the catalog that answers a committed save.
    private async Task<(JsonElement Before, JsonElement After)> Save(HttpClient client, string json)
    {
        var before = await Read(client);
        var (status, after) = await Save(client, Revision(before), json);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {after.GetRawText()}");
        return (before, after);
    }

    private async Task<JsonNode> FileDocument() => JsonNode.Parse(await File.ReadAllTextAsync(FilePath, Ct))!;

    // Whether a document holds a key as an explicit null, which is not the same as not holding it.
    private static bool IsNullMember(JsonNode document, string key)
    {
        var parts = key.Split(':');
        JsonNode? current = document;
        foreach (var part in parts.SkipLast(1)) current = current is JsonObject owner ? owner[part] : null;
        return current is JsonObject parent && parent.TryGetPropertyValue(parts[^1], out var value) && value is null;
    }

    private void Seed(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, json);
    }

    [Fact]
    public async Task Catalog_DescribesEveryKey_AndNeverReturnsASecretValue()
    {
        using var factory = Factory(Configured);
        using var client = Admin(factory);
        var catalog = await Read(client);
        Assert.Equal(ConfigurationCatalog.Fields.Select(field => field.Key),
            catalog.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("key").GetString()));
        Assert.Equal(ConfigurationCatalog.Groups.Select(group => group.Id),
            catalog.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("id").GetString()));
        Assert.False(catalog.GetProperty("fileExists").GetBoolean());
        Assert.False(catalog.GetProperty("externallyModified").GetBoolean());
        Assert.Empty(catalog.GetProperty("unknownFileKeys").EnumerateArray());
        foreach (var definition in ConfigurationCatalog.Fields)
        {
            var field = Field(catalog, definition.Key);
            Assert.Equal(definition.Group, field.GetProperty("group").GetString());
            if (definition.Apply == ConfigurationApply.ReadOnly)
            {
                Assert.False(field.GetProperty("editable").GetBoolean());
                Assert.False(string.IsNullOrEmpty(field.GetProperty("reason").GetString()));
            }
            if (definition.Secret)
            {
                // Presence only: no serialized form of a secret ever leaves the host.
                Assert.Contains(field.GetProperty("hasValue").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
                foreach (var name in new[] { "value", "raw", "savedValue", "defaultValue" })
                    Assert.Equal(JsonValueKind.Null, field.GetProperty(name).ValueKind);
            }
        }
        Assert.True(Field(catalog, Secret).GetProperty("hasValue").GetBoolean());
        Assert.False(Field(catalog, "Audio:AccessKey").GetProperty("hasValue").GetBoolean());
        Assert.False(Field(catalog, "Models:Reasoning:ApiKey").GetProperty("hasValue").GetBoolean());
        // A restart-only secret is told by presence on each side of the restart; a live one has no next start to report.
        Assert.True(Field(catalog, Secret).GetProperty("savedHasValue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Field(catalog, "Audio:AccessKey").GetProperty("savedHasValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(catalog, "Models:Reasoning:ApiKey").GetProperty("savedHasValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(catalog, "Audio:SecretKey").GetProperty("savedHasValue").ValueKind);
        // The server and the database alone: the credential a PostgreSQL connection string
        // carries is reported nowhere, neither as the value nor as the raw text.
        var connection = new NpgsqlConnectionStringBuilder(factory.DatabaseConnectionString);
        var database = Field(catalog, "ConnectionStrings:Default");
        Assert.Equal(new NpgsqlConnectionStringBuilder { Host = connection.Host, Port = connection.Port, Database = connection.Database }.ConnectionString,
            database.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, database.GetProperty("raw").ValueKind);
        Assert.Equal("environment", database.GetProperty("source").GetString());
        Assert.DoesNotContain(connection.Password!, catalog.GetRawText());
        Assert.DoesNotContain("Password", catalog.GetRawText());
        Assert.Equal("PostgreSQL", Fact(catalog, "database").GetProperty("value").GetString());
        Assert.Equal(factory.Services.GetRequiredService<IConfiguration>()["IdentityService:Authority"],
            Field(catalog, "IdentityService:Authority").GetProperty("value").GetString());
        Assert.Equal("restart", Field(catalog, "IdentityService:Authority").GetProperty("apply").GetString());
        Assert.Equal("live", Field(catalog, "RateLimits:AdminLogin:Enabled").GetProperty("apply").GetString());
        Assert.Equal("live", Field(catalog, "Models:Reasoning:Endpoint").GetProperty("apply").GetString());
        Assert.Equal("readOnly", Field(catalog, "DataProtection:RootKey").GetProperty("apply").GetString());
        Assert.Equal("5008", Fact(catalog, "httpPort").GetProperty("value").GetString());
        // Only a number carries bounds: a choice or a text has none to state.
        Assert.Equal(-12, Field(catalog, "Models:Speech:Uk:Pitch").GetProperty("minimum").GetInt32());
        // A decimal default is a number, not text and not nothing.
        Assert.Equal(0.9m, Field(catalog, "VocabularyLearning:Fsrs:DesiredRetention").GetProperty("value").GetDecimal());
        Assert.Equal(0.9m, Field(catalog, "VocabularyLearning:Fsrs:DesiredRetention").GetProperty("defaultValue").GetDecimal());
        Assert.Equal(7m, Field(catalog, "VocabularyLearning:Fsrs:FamiliarThresholdDays").GetProperty("defaultValue").GetDecimal());
        // A review-step list is shown as the extras configured after the built-in steps: none by default.
        Assert.Empty(Field(catalog, "VocabularyLearning:Fsrs:LearningStepsMinutes").GetProperty("value").EnumerateArray());
        Assert.Empty(Field(catalog, "VocabularyLearning:Fsrs:LearningStepsMinutes").GetProperty("defaultValue").EnumerateArray());
        Assert.Equal("integerList", Field(catalog, "VocabularyLearning:Fsrs:LearningStepsMinutes").GetProperty("kind").GetString());
        Assert.Empty(Field(catalog, "Network:TrustedProxies").GetProperty("value").EnumerateArray());
        foreach (var key in new[] { "Models:Speech:Endpoint", "Models:Reasoning:Endpoint", "IdentityService:Authority" })
        {
            Assert.Equal(JsonValueKind.Null, Field(catalog, key).GetProperty("minimum").ValueKind);
            Assert.Equal(JsonValueKind.Null, Field(catalog, key).GetProperty("maximum").ValueKind);
        }
        // A model connection is shown under the key of this release; the module is asked by the name it keeps.
        Assert.Equal("enrichment.text.endpoint", Field(catalog, "Models:Reasoning:Endpoint").GetProperty("wire").GetString());
        Assert.Equal(JsonValueKind.Null, Field(catalog, "Models:Reasoning:Endpoint").GetProperty("location").ValueKind);
        Assert.Equal("Testing", Fact(catalog, "environment").GetProperty("value").GetString());
        Assert.Equal("data/appsettings.json", Fact(catalog, "configurationFile").GetProperty("value").GetString());
        Assert.Equal("configured", Fact(catalog, "administratorSignIn").GetProperty("value").GetString());
        Assert.Equal("pending", Fact(catalog, "enrichmentConfigurationReady").GetProperty("value").GetString());

        // A live key's value is its module's own answer.
        using var limits = JsonDocument.Parse(await client.GetStringAsync("/admin/system/settings/rate-limits", Ct));
        Assert.Equal(limits.RootElement.GetProperty("adminLogin").GetProperty("enabled").GetBoolean(),
            Field(catalog, "RateLimits:AdminLogin:Enabled").GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task AModelConnectionFromBeforeTheUpgrade_IsShownUnderThisReleasesKey_WithWhereTheFileHoldsIt()
    {
        // A deployment whose reasoning model was configured under the section an earlier
        // release used: this release still reads it there.
        Seed("""{"Enrichment:Text":{"Endpoint":"https://api.example.test/v1","Model":"reasoning-1"}}""");
        using var factory = Factory();
        using var client = Admin(factory);
        var catalog = await Read(client);
        var endpoint = Field(catalog, "Models:Reasoning:Endpoint");
        Assert.Equal("https://api.example.test/v1", endpoint.GetProperty("value").GetString());
        Assert.Equal("file", endpoint.GetProperty("source").GetString());
        Assert.Equal("Enrichment:Text:Endpoint", endpoint.GetProperty("location").GetString());
        // The running connection is the legacy one, wherever the file keeps it.
        Assert.Equal("reasoning-1", factory.Services.GetRequiredService<EnrichmentRuntimeConfiguration>().Current.Enrichment.Text.Model);
        // A key this release still reads is not an unknown one.
        Assert.Empty(catalog.GetProperty("unknownFileKeys").EnumerateArray());
    }

    [Fact]
    public async Task StartupSave_PersistsForTheNextStart_WithoutTouchingTheRunningProcess()
    {
        const string body = """{"Logging:LogLevel:Default":"Debug","Service:InstanceId":"lexarbor-stable","Network:TrustedProxies":["172.18.0.2","[::1]"]}""";
        string[] keys = ["Logging:LogLevel:Default", "Service:InstanceId", "Network:TrustedProxies"];
        JsonElement after;
        using (var factory = Factory())
        using (var client = Admin(factory))
        {
            var (before, saved) = await Save(client, body);
            after = saved;
            Assert.NotEqual(Revision(before), Revision(after));
            foreach (var key in keys)
            {
                Assert.True(Field(after, key).GetProperty("pendingRestart").GetBoolean());
                // The running value is unchanged; the saved one waits for the restart.
                Assert.Equal(Field(before, key).GetProperty("value").GetRawText(), Field(after, key).GetProperty("value").GetRawText());
            }
            Assert.Equal("Debug", Field(after, "Logging:LogLevel:Default").GetProperty("savedValue").GetString());
            Assert.Equal("lexarbor-stable", Field(after, "Service:InstanceId").GetProperty("savedValue").GetString());
            Assert.Equal("[::1]", Field(after, "Network:TrustedProxies").GetProperty("savedValue")[1].GetString());
            var configuration = factory.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("Information", configuration["Logging:LogLevel:Default"]);
            Assert.Null(configuration["Service:InstanceId"]);
            var file = await FileDocument();
            Assert.Equal("Debug", file["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
            Assert.Equal("lexarbor-stable", file["Service"]!["InstanceId"]!.GetValue<string>());
        }
        using var restarted = Factory();
        using var restartedClient = Admin(restarted);
        var reloaded = await Read(restartedClient);
        foreach (var key in keys)
        {
            Assert.False(Field(reloaded, key).GetProperty("pendingRestart").GetBoolean());
            Assert.Equal("file", Field(reloaded, key).GetProperty("source").GetString());
            Assert.Equal(Field(after, key).GetProperty("savedValue").GetRawText(), Field(reloaded, key).GetProperty("value").GetRawText());
        }
        Assert.Equal("lexarbor-stable", restarted.Services.GetRequiredService<IConfiguration>()["Service:InstanceId"]);
    }

    [Fact]
    public async Task ASavedCandidate_StartsTheNextHost_AndTakesEffectThere()
    {
        const string body = """
            {
              "IdentityService:Authority": "https://signacore.example.com",
              "IdentityService:Issuer": "https://signacore.example.com",
              "IdentityService:Audience": "lexarbor-admin",
              "IdentityService:RequireHttpsMetadata": true,
              "AdminAuthentication:RequiredRole": "administrator",
              "AdminAuthentication:OidcCode:Scope": "openid profile",
              "AdminAuthentication:OidcCode:RedirectUri": "https://lexarbor.example.com/admin/auth/callback",
              "AdminAuthentication:OidcCode:PostLogoutRedirectUri": "https://lexarbor.example.com/admin/auth/logout/return",
              "RateLimits:AdminLogin:PermitLimit": 25,
              "RateLimits:AdminLogin:WindowSeconds": 120,
              "Network:TrustedProxies": ["172.18.0.2", "10.0.0.1"],
              "Network:TrustedNetworks": ["172.18.0.0/16", "2001:db8::/32"],
              "Network:ForwardLimit": 2,
              "Audio:PlaybackTtlSeconds": 600,
              "Audio:MaxUploadBytes": 2097152,
              "Audio:MaxConcurrentUploads": 4,
              "Audio:MaxQueuedUploads": 4,
              "Audio:OperationTimeoutSeconds": 90,
              "Audio:RecoveryLeaseSeconds": 400,
              "Audio:RecoveryMaxRetrySeconds": 7200,
              "Logging:LogLevel:Default": "Debug",
              "Logging:LogLevel:Microsoft.AspNetCore": "Information",
              "MeaningSubmissions:BookId": "book-1",
              "Database:SlowCommandThresholdMilliseconds": 250,
              "VocabularyLearning:SlowAnswerMs": 10000,
              "VocabularyLearning:QuestionExpiryMinutes": 45,
              "VocabularyLearning:TimeZone": "Asia/Shanghai",
              "VocabularyLearning:Fsrs:DesiredRetention": 0.85,
              "VocabularyLearning:Fsrs:LearningStepsMinutes": [2, 20],
              "VocabularyLearning:ErrorProne:WindowDays": 60,
              "Lexarbor:BaseUrl": "https://lexarbor.example.test",
              "Lexarbor:ApiKey": { "action": "replace", "value": "synthetic-catalog-key" },
              "Lexarbor:Integration:ApiKey": { "action": "replace", "value": "synthetic-submission-key" },
              "Service:InstanceId": "lexarbor-production-1"
            }
            """;
        using (var factory = Factory(Configured))
        using (var client = Admin(factory))
        {
            var (_, after) = await Save(client, body);
            // Everything saved is waiting for the restart, except a value equal to the running one.
            string[] alreadyInEffect = ["AdminAuthentication:OidcCode:Scope"];
            Assert.Equal(JsonNode.Parse(body)!.AsObject().Select(property => property.Key).Where(key => !alreadyInEffect.Contains(key))
                    .OrderBy(key => key, StringComparer.Ordinal),
                ConfigurationCatalog.RestartFields.Where(field => Field(after, field.Key).GetProperty("pendingRestart").GetBoolean())
                    .Select(field => field.Key).OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal("lexarbor-production-1", Field(after, "Service:InstanceId").GetProperty("savedValue").GetString());
            // A decimal keeps its fraction, a list of steps is a list of numbers, and a secret is
            // presence only.
            Assert.Equal(0.85m, Field(after, "VocabularyLearning:Fsrs:DesiredRetention").GetProperty("savedValue").GetDecimal());
            Assert.Equal(new[] { 2, 20 }, Field(after, "VocabularyLearning:Fsrs:LearningStepsMinutes").GetProperty("savedValue").EnumerateArray().Select(item => item.GetInt32()));
            Assert.Equal(JsonValueKind.Null, Field(after, "Lexarbor:ApiKey").GetProperty("savedValue").ValueKind);
            Assert.DoesNotContain("synthetic-catalog-key", after.GetRawText());
            var savedFile = await FileDocument();
            Assert.Equal(0.85m, savedFile["VocabularyLearning"]!["Fsrs"]!["DesiredRetention"]!.GetValue<decimal>());
            Assert.Equal(20, savedFile["VocabularyLearning"]!["Fsrs"]!["LearningStepsMinutes"]![1]!.GetValue<int>());
            Assert.Equal("synthetic-catalog-key", savedFile["Lexarbor"]!["ApiKey"]!.GetValue<string>());
        }
        // The next host reads the file. Its identity keys also carry the test host's own
        // defaults, which stand here for a deployment's environment. The saved role is in
        // force there, so only a token carrying it may administer the restarted host.
        var restarted = Factory(Configured);
        using (restarted)
        using (var client = Admin(restarted, "administrator"))
        {
            var reloaded = await Read(client);
            // The test host's own in-memory identity and sign-in values outrank the file, so
            // those keys keep comparing against them; every other saved key is in force now.
            string[] hostDefaults =
            [
                "IdentityService:Authority", "IdentityService:Issuer", "IdentityService:Audience", "IdentityService:RequireHttpsMetadata",
                Redirect
            ];
            Assert.DoesNotContain(reloaded.GetProperty("fields").EnumerateArray(), field =>
                field.GetProperty("pendingRestart").GetBoolean() && !hostDefaults.Contains(field.GetProperty("key").GetString()));
            var configuration = restarted.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("Debug", configuration["Logging:LogLevel:Default"]);
            Assert.Equal("Information", configuration["Logging:LogLevel:Microsoft.AspNetCore"]);
            Assert.Equal("lexarbor-production-1", configuration["Service:InstanceId"]);
            Assert.Equal("25", configuration["RateLimits:AdminLogin:PermitLimit"]);
            Assert.Equal("172.18.0.2", configuration["Network:TrustedProxies:0"]);
            Assert.Equal("2001:db8::/32", configuration["Network:TrustedNetworks:1"]);
            Assert.Equal("600", configuration["Audio:PlaybackTtlSeconds"]);
            Assert.Equal("book-1", configuration["MeaningSubmissions:BookId"]);
            Assert.Equal("https://lexarbor.example.com/admin/auth/logout/return", configuration["AdminAuthentication:OidcCode:PostLogoutRedirectUri"]);
            Assert.Equal(25, restarted.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.AdminLogin.PermitLimit);
            Assert.Equal("administrator", restarted.Services.GetRequiredService<IOptionsMonitor<AdminAuthenticationOptions>>().CurrentValue.RequiredRole);
            Assert.True(restarted.Services.GetRequiredService<AdminOidcSettings>().Available);
            Assert.Equal("https://lexarbor.example.com/admin/auth/logout/return", restarted.Services.GetRequiredService<AdminOidcSettings>().PostLogoutRedirectUri);
            // The groups this release added are bound from what was saved, by their own options.
            var learning = restarted.Services.GetRequiredService<IOptions<VocabularyLearningOptions>>().Value;
            Assert.Equal(10000, learning.SlowAnswerMs);
            Assert.Equal(45, learning.QuestionExpiryMinutes);
            Assert.Equal("Asia/Shanghai", learning.TimeZone);
            var scheduling = restarted.Services.GetRequiredService<IOptions<VocabularyFsrsOptions>>().Value;
            Assert.Equal(0.85, scheduling.DesiredRetention);
            // The options binder adds what was saved after the built-in steps, which is why the
            // page shows and edits these lists as the extras.
            Assert.Equal(new[] { 1, 10, 2, 20 }, scheduling.LearningStepsMinutes);
            Assert.Equal(60, restarted.Services.GetRequiredService<IOptions<VocabularyErrorProneOptions>>().Value.WindowDays);
            var catalogClient = restarted.Services.GetRequiredService<IOptions<LexarborOptions>>().Value;
            Assert.Equal("https://lexarbor.example.test", catalogClient.BaseUrl);
            Assert.Equal("synthetic-catalog-key", catalogClient.ApiKey);
            Assert.Equal("synthetic-submission-key", restarted.Services.GetRequiredService<LexarborIntegrationOptions>().ApiKey);
            Assert.Equal(0.85m, Field(reloaded, "VocabularyLearning:Fsrs:DesiredRetention").GetProperty("value").GetDecimal());
            Assert.Equal(new[] { 2, 20 }, Field(reloaded, "VocabularyLearning:Fsrs:LearningStepsMinutes").GetProperty("value").EnumerateArray().Select(item => item.GetInt32()));
        }
        // The saved sign-in section, with the client credentials a deployment supplies itself,
        // passes startup's own hosted-login rules.
        using var file = new MemoryStream(await File.ReadAllBytesAsync(FilePath, Ct));
        var saved = new ConfigurationBuilder().AddJsonStream(file).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminAuthentication:OidcCode:ClientId"] = "lexarbor-admin",
            ["AdminAuthentication:OidcCode:ClientSecret"] = "synthetic-client-secret"
        }).Build();
        var signIn = new AdminOidcSettings();
        signIn.Load(saved);
        Assert.True(signIn.Available);
        Assert.Equal("https://lexarbor.example.com/admin/auth/callback", signIn.RedirectUri);
        Assert.Equal("https://signacore.example.com", signIn.Authority);
    }

    [Theory]
    [InlineData("""{"IdentityService:Authority":"not a uri"}""", "IdentityService:Authority")]
    [InlineData("""{"IdentityService:RequireHttpsMetadata":"maybe"}""", "IdentityService:RequireHttpsMetadata")]
    [InlineData("""{"Network:ForwardLimit":11}""", "Network:ForwardLimit")]
    [InlineData("""{"Network:ForwardLimit":2}""", "Network:TrustedProxies")]
    [InlineData("""{"Logging:LogLevel:Default":"Chatty"}""", "Logging:LogLevel:Default")]
    [InlineData("""{"Audio:RecoveryLeaseSeconds":60}""", "Audio:RecoveryLeaseSeconds")]
    [InlineData("""{"Audio:MaxQueuedUploads":9}""", "Audio:MaxQueuedUploads")]
    [InlineData("""{"VocabularyLearning:Fsrs:DesiredRetention":1}""", "VocabularyLearning:Fsrs:DesiredRetention")]
    [InlineData("""{"VocabularyLearning:Fsrs:MasteredThresholdDays":3}""", "VocabularyLearning:Fsrs:MasteredThresholdDays")]
    [InlineData("""{"VocabularyLearning:Fsrs:LearningStepsMinutes":[5,0]}""", "VocabularyLearning:Fsrs:LearningStepsMinutes")]
    [InlineData("""{"VocabularyLearning:TimeZone":"Mars/Olympus"}""", "VocabularyLearning:TimeZone")]
    [InlineData("""{"Lexarbor:Integration:ApiKey":{"action":"replace","value":"synthetic-submission-key"}}""", "Lexarbor:BaseUrl")]
    [InlineData("""{"Lexarbor:BaseUrl":"http://lexarbor.example.test","Lexarbor:ApiKey":{"action":"replace","value":"synthetic-catalog-key"}}""",
        "Lexarbor:AllowInsecureHttp")]
    [InlineData("""{"Database:LegacySqlitePath":"appsettings.json"}""", "Database:LegacySqlitePath")]
    [InlineData("""{"Lexarbor:ApiKey":{"action":"replace","value":"synthetic,key"}}""", "Lexarbor:ApiKey")]
    public async Task AnInvalidCandidate_IsRefusedBeforeAnythingIsWritten(string body, string field)
    {
        using var factory = Factory(); using var client = Admin(factory);
        var before = await Read(client);
        var (status, answer) = await Save(client, Revision(before), body);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid startup settings.", answer.GetProperty("message").GetString());
        Assert.Contains(answer.GetProperty("fields").EnumerateArray(), item => item.GetString() == field);
        // Keys are named; the refused values never are.
        Assert.DoesNotContain("not a uri", answer.GetRawText());
        Assert.False(File.Exists(FilePath));
        Assert.Equal(Revision(before), Revision(await Read(client)));
    }

    [Fact]
    public async Task AWorkingSignIn_CannotBeLeftIncomplete_AndItsSecretStaysHidden()
    {
        using var factory = Factory(Configured); using var client = Admin(factory);
        var before = await Read(client);
        foreach (var body in new[]
                 {
                     """{"AdminAuthentication:OidcCode:ClientSecret":{"action":"clear"}}""",
                     """{"AdminAuthentication:OidcCode:ClientId":""}""",
                     """{"AdminAuthentication:OidcCode:RedirectUri":"https://admin.example.test/other"}"""
                 })
        {
            var (status, answer) = await Save(client, Revision(before), body);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.NotEmpty(answer.GetProperty("fields").EnumerateArray());
        }
        Assert.False(File.Exists(FilePath));
        var (status2, after) = await Save(client, Revision(before),
            """{"AdminAuthentication:OidcCode:ClientSecret":{"action":"replace","value":"synthetic-rotated-secret"},"AdminAuthentication:OidcCode:RedirectUri":"https://lexarbor.example.com/admin/auth/callback"}""");
        Assert.Equal(HttpStatusCode.OK, status2);
        var secret = Field(after, Secret);
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("savedValue").ValueKind);
        Assert.True(secret.GetProperty("hasValue").GetBoolean());
        // The next start finds the replacement: said by presence, never by its value.
        Assert.True(secret.GetProperty("savedHasValue").GetBoolean());
        Assert.True(secret.GetProperty("pendingRestart").GetBoolean());
        Assert.DoesNotContain("synthetic-rotated-secret", after.GetRawText());
        var file = await FileDocument();
        Assert.Equal("synthetic-rotated-secret", file["AdminAuthentication"]!["OidcCode"]!["ClientSecret"]!.GetValue<string>());
        // The running trust is the one this process started with.
        Assert.Equal("vocabulary-client-secret", factory.Services.GetRequiredService<AdminOidcSettings>().ClientSecret);
        Assert.Equal("https://admin.example.test/admin/auth/callback", factory.Services.GetRequiredService<AdminOidcSettings>().RedirectUri);
    }

    [Fact]
    public async Task ANullRemovesTheKey_SoTheNextStartReadsTheDefault()
    {
        Seed("""{"Service":{"InstanceId":"from-file"},"Logging":{"LogLevel":{"Default":"Warning","Microsoft.AspNetCore":"Warning"}}}""");
        using var factory = Factory(); using var client = Admin(factory);
        var before = await Read(client);
        Assert.Equal("from-file", Field(before, "Service:InstanceId").GetProperty("value").GetString());
        Assert.Equal("file", Field(before, "Service:InstanceId").GetProperty("source").GetString());
        var (status, after) = await Save(client, Revision(before), """{"Service:InstanceId":null,"Logging:LogLevel:Microsoft.AspNetCore":null}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var file = await FileDocument();
        // The removed key is gone, its untouched sibling keeps its section.
        Assert.Null(file["Service"]);
        Assert.Null(file["Logging"]!["LogLevel"]!["Microsoft.AspNetCore"]);
        Assert.Equal("Warning", file["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
        var instance = Field(after, "Service:InstanceId");
        // A random ID per start replaces the file's; the template's Warning equals the running value.
        Assert.True(instance.GetProperty("pendingRestart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, instance.GetProperty("savedValue").ValueKind);
        Assert.Equal("default", instance.GetProperty("source").GetString());
        Assert.False(Field(after, "Logging:LogLevel:Microsoft.AspNetCore").GetProperty("pendingRestart").GetBoolean());
        Assert.Equal("Warning", Field(after, "Logging:LogLevel:Microsoft.AspNetCore").GetProperty("savedValue").GetString());
    }

    [Fact]
    public async Task ValuesWaitingForARestart_DoNotReachTheLiveModules()
    {
        using var factory = Factory(); using var client = Admin(factory);
        var (_, audio) = await Save(client, """{"Audio:MaxConcurrentUploads":8,"Audio:RecoveryIntervalSeconds":120}""");
        Assert.True(Field(audio, "Audio:MaxConcurrentUploads").GetProperty("pendingRestart").GetBoolean());
        // The live module validates against the limits this process runs with, so it still
        // accepts a save, and its commit keeps the values waiting for the restart.
        using var storage = new HttpRequestMessage(HttpMethod.Put, "/admin/system/settings/audio-storage")
        { Content = new StringContent(Storage, Encoding.UTF8, "application/json") };
        storage.Headers.TryAddWithoutValidation("If-Match", Revision(await Read(client)));
        using var stored = await client.SendAsync(storage, Ct);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        var file = await FileDocument();
        Assert.Equal(8, file["Audio"]!["MaxConcurrentUploads"]!.GetValue<int>());
        Assert.Equal(120, file["Audio"]!["RecoveryIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task ALiveModuleMayClearANullableValue_AndTheFileKeepsTheNullItSubmitted()
    {
        using var factory = Factory(); using var client = Admin(factory);
        async Task SaveSpeech(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/enrichment/settings")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("If-Match", Revision(await Read(client)));
            using var answer = await client.SendAsync(request, Ct);
            Assert.True(answer.StatusCode == HttpStatusCode.OK, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync(Ct)}");
        }
        var speech = factory.Services.GetRequiredService<EnrichmentRuntimeConfiguration>();
        await SaveSpeech("""{"enrichment":{"tts":{"uk":{"speed":0.9}}}}""");
        Assert.Equal(0.9m, speech.Current.Enrichment.Uk.Speed);
        await SaveSpeech("""{"enrichment":{"tts":{"uk":{"speed":null}}}}""");
        // The cleared key reads as cleared, not as the value it had a moment before: the module's
        // null is a value of its own, unlike the null that removes a restart-only key from the file.
        Assert.Null(speech.Current.Enrichment.Uk.Speed);
        // On disk the null sits where the connection lives: in its own section, or in the one a save moves it to.
        var file = await FileDocument();
        var physical = factory.Services.GetRequiredService<PersistentConfigurationCoordinator>().PhysicalKey("Enrichment:Tts:Uk:Speed");
        Assert.True(IsNullMember(file, physical) || IsNullMember(file, "Models:Speech:Uk:Speed"));
    }

    [Fact]
    public async Task ALiveSwitch_IsStillJudgedAgainstTheNumbersThisProcessRuns()
    {
        // Numbers the limiter cannot use; the policy is disabled, so startup tolerates them.
        using var factory = Factory(new()
        {
            ["RateLimits:AdminLogin:PermitLimit"] = "-1",
            ["RateLimits:AdminLogin:WindowSeconds"] = "601"
        });
        using var client = Admin(factory);
        var (_, saved) = await Save(client, """{"RateLimits:AdminLogin:PermitLimit":10,"RateLimits:AdminLogin:WindowSeconds":60}""");
        Assert.True(Field(saved, "RateLimits:AdminLogin:PermitLimit").GetProperty("pendingRestart").GetBoolean());
        // Enabling would hand the limiter the invalid numbers until the restart.
        using var enable = new HttpRequestMessage(HttpMethod.Put, "/admin/system/settings/rate-limits")
        { Content = new StringContent("""{"adminLoginEnabled":true}""", Encoding.UTF8, "application/json") };
        enable.Headers.TryAddWithoutValidation("If-Match", Revision(await Read(client)));
        using var refused = await client.SendAsync(enable, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task ADeploymentOverride_IsReadOnly_AndNamedAsSuch()
    {
        // The file holds an older value for the same key; the deployment's wins at every start.
        Seed("""{"Service":{"InstanceId":"from-file"}}""");
        using var baseFactory = Factory();
        // A synthetic provider instance avoids process-global environment mutations.
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.Add(new EnvironmentSource("Service:InstanceId", "from-environment"))));
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.CreateToken("admin"));
        var catalog = await Read(client);
        var instance = Field(catalog, "Service:InstanceId");
        Assert.False(instance.GetProperty("editable").GetBoolean());
        Assert.Equal("environment", instance.GetProperty("source").GetString());
        Assert.Equal("Overridden by deployment configuration.", instance.GetProperty("reason").GetString());
        Assert.Equal("from-environment", instance.GetProperty("value").GetString());
        // Nothing the file says about it changes the next start.
        Assert.False(instance.GetProperty("pendingRestart").GetBoolean());
        Assert.Equal("from-environment", instance.GetProperty("savedValue").GetString());
        var (status, answer) = await Save(client, Revision(catalog), """{"Service:InstanceId":"lexarbor-edited"}""");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(new[] { "Service:InstanceId" }, answer.GetProperty("fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("from-file", (await FileDocument())["Service"]!["InstanceId"]!.GetValue<string>());
    }

    private sealed class EnvironmentSource(string key, string value) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder _) => new EnvironmentProvider(key, value);
    }

    private sealed class EnvironmentProvider(string key, string value)
        : Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider
    {
        public override void Load() => Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [key] = value };
    }

    [Fact]
    public async Task RevisionIsRequired_AndAStaleOneIsRefused()
    {
        using var factory = Factory(); using var client = Admin(factory);
        var before = await Read(client);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Save(client, null, """{"Service:InstanceId":"a"}""")).Status);
        foreach (var value in new[] { "*", "W/" + Revision(before), "bad" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Save(client, value, """{"Service:InstanceId":"a"}""")).Status);
        var answers = await Task.WhenAll(Save(client, Revision(before), """{"Service:InstanceId":"first"}"""),
            Save(client, Revision(before), """{"Service:InstanceId":"second"}"""));
        var winner = Assert.Single(answers, answer => answer.Status == HttpStatusCode.OK);
        var stale = Assert.Single(answers, answer => answer.Status == HttpStatusCode.PreconditionFailed);
        Assert.Equal("Settings changed. Reload settings; offline edits require a restart.", stale.Body.GetProperty("message").GetString());
        // The file holds the winner's value; the refused one never arrived.
        var committed = Field(winner.Body, "Service:InstanceId").GetProperty("savedValue").GetString();
        Assert.Equal(committed, (await FileDocument())["Service"]!["InstanceId"]!.GetValue<string>());
        Assert.DoesNotContain(committed == "first" ? "second" : "first", await File.ReadAllTextAsync(FilePath, Ct));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("""{"Audio:Enabled":true}""")]
    [InlineData("""{"Unknown:Key":1}""")]
    [InlineData("""{"Logging:LogLevel:Default":"Debug","logging:loglevel:default":"Warning"}""")]
    [InlineData("""{"Logging:LogLevel:Default":true}""")]
    [InlineData("""{"Audio:MaxQueuedUploads":"8"}""")]
    [InlineData("""{"Network:TrustedProxies":"10.0.0.1"}""")]
    [InlineData("""{"Network:TrustedProxies":[1]}""")]
    [InlineData("""{"Network:TrustedProxies":[""]}""")]
    [InlineData("""{"Audio:MaxUploadBytes":20971521}""")]
    [InlineData("""{"AdminAuthentication:OidcCode:ClientSecret":"synthetic-plain-secret"}""")]
    [InlineData("""{"AdminAuthentication:OidcCode:ClientSecret":{"action":"replace"}}""")]
    [InlineData("""{"AdminAuthentication:OidcCode:ClientSecret":{"action":"replace","value":" "}}""")]
    [InlineData("""{"AdminAuthentication:OidcCode:ClientSecret":{"action":"replace","value":"x","extra":"y"}}""")]
    [InlineData("""{"AdminAuthentication:OidcCode:ClientSecret":{"action":"keep","value":"x"}}""")]
    [InlineData("""{"AdminAuthentication:OidcCode:ClientSecret":{"action":"keep"}}""")]
    [InlineData("""{"AdminAuthentication:OidcCode:ClientSecret":{"action":"rotate","value":"x"}}""")]
    [InlineData("""{"Database:InitializeOnStartup":null}""")]
    [InlineData("""{"VocabularyLearning:Fsrs:LearningStepsMinutes":[]}""")]
    [InlineData("""{"VocabularyLearning:Fsrs:LearningStepsMinutes":[1,"2"]}""")]
    [InlineData("""{"VocabularyLearning:Fsrs:LearningStepsMinutes":"1,10"}""")]
    [InlineData("""{"VocabularyLearning:Fsrs:RelearningStepsMinutes":[0]}""")]
    [InlineData("""{"VocabularyLearning:Fsrs:DesiredRetention":"0.9"}""")]
    [InlineData("""{"Database:SlowCommandThresholdMilliseconds":"250"}""")]
    [InlineData("""{"Lexarbor:ApiKey":"synthetic-plain-key"}""")]
    public async Task AMalformedRequest_IsRefusedWithoutEchoingInput(string body)
    {
        using var factory = Factory(); using var client = Admin(factory);
        var before = await Read(client);
        var (status, answer) = await Save(client, Revision(before), body);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid startup settings.", answer.GetProperty("message").GetString());
        Assert.DoesNotContain("synthetic-plain", answer.GetRawText());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AnOversizedBody_IsRefusedBeforeParsing()
    {
        using var factory = Factory(); using var client = Admin(factory);
        var (status, answer) = await Save(client, Revision(await Read(client)), "{\"Logging:LogLevel:Default\":\"" + new string('x', 17000) + "\"}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("Settings request is too large.", answer.GetProperty("message").GetString());
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("user", 403)]
    [InlineData("admin", 200)]
    public async Task BothMethods_RequireAnAdministrator_AndAnswerWithoutCaching(string? role, int expected)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(role));
        using var catalog = await client.GetAsync(CatalogUrl, Ct);
        using var request = new HttpRequestMessage(HttpMethod.Put, StartupUrl)
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        using var save = await client.SendAsync(request, Ct);
        // An administrator reaches the handler, which refuses the empty body.
        var expectedSave = role == "admin" ? 400 : expected;
        foreach (var (response, status) in new[] { (catalog, expected), (save, expectedSave) })
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    [Fact]
    public async Task ACookieSession_NeedsTheAntiforgeryToken()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie());
        var revision = Revision(await Read(client));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Save(client, revision, """{"Service:InstanceId":"a"}""")).Status);
        Assert.False(File.Exists(FilePath));
        AdminTestAntiforgery.Attach(client, factory);
        Assert.Equal(HttpStatusCode.OK, (await Save(client, revision, """{"Service:InstanceId":"a"}""")).Status);
    }

    [Fact]
    public async Task UnmanagedKeys_AreNamedNeverShown_AndAnOutsideEditBlocksSaves()
    {
        Seed("""{"Logging":{"LogLevel":{"Default":"Warning"}},"Custom":{"Token":"synthetic-unknown-value"}}""");
        using var factory = Factory(); using var client = Admin(factory);
        var catalog = await Read(client);
        Assert.Equal(new[] { "Custom:Token" }, catalog.GetProperty("unknownFileKeys").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("synthetic-unknown-value", catalog.GetRawText());
        Assert.Equal("Warning", Field(catalog, "Logging:LogLevel:Default").GetProperty("value").GetString());
        Assert.True(catalog.GetProperty("fileExists").GetBoolean());

        // An edit made outside the host is the operator's: it is reported, never overwritten.
        File.WriteAllText(FilePath, """{"Logging":{"LogLevel":{"Default":"Error"}}}""");
        var edited = await Read(client);
        Assert.True(edited.GetProperty("externallyModified").GetBoolean());
        Assert.Equal("Warning", Field(edited, "Logging:LogLevel:Default").GetProperty("value").GetString());
        Assert.True(Field(edited, "Logging:LogLevel:Default").GetProperty("pendingRestart").GetBoolean());
        Assert.Equal("Error", Field(edited, "Logging:LogLevel:Default").GetProperty("savedValue").GetString());
        var (status, _) = await Save(client, Revision(edited), """{"Service:InstanceId":"a"}""");
        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal("Error", (await FileDocument())["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_KeepsItsBytesAndTheRunningValues()
    {
        using var factory = Factory(); using var client = Admin(factory);
        await Save(client, """{"Service:InstanceId":"first"}""");
        var before = await Read(client);
        var bytes = await File.ReadAllBytesAsync(FilePath, Ct);
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        try
        {
            var (status, answer) = await Save(client, Revision(before), """{"Service:InstanceId":"second"}""");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Equal("Persistent configuration is unavailable.", answer.GetProperty("message").GetString());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(FilePath, Ct));
            Assert.Equal(Revision(before), Revision(await Read(client)));
        }
        finally { File.SetAttributes(FilePath, FileAttributes.Normal); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
