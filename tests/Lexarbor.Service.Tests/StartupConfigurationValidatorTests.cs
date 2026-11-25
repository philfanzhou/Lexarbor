using System.Text.Json.Nodes;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The rule table of the next-start candidate: what startup would refuse, the settings
/// surface refuses first, naming the keys and never the values.
/// </summary>
public sealed class StartupConfigurationValidatorTests
{
    private const string Authority = "IdentityService:Authority";
    private const string Redirect = "AdminAuthentication:OidcCode:RedirectUri";
    private const string ClientSecret = "AdminAuthentication:OidcCode:ClientSecret";
    private const string Traces = "Telemetry:Otlp:Traces";
    private static readonly string[] Advanced = ConfigurationCatalog.RestartFields
        .Where(field => field.Group == "audio-storage").Select(field => field.Key).ToArray();

    private static IReadOnlyList<string> Invalid(Dictionary<string, string?>? values, bool signInConfigured = true, string? authorization = null)
        => new StartupConfigurationValidator(new TestEnvironment(),
            new AdminOidcSettings { Available = signInConfigured }, () => authorization)
            .InvalidFields(Configuration(values));

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Lexarbor.Service.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? values) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["IdentityService:Authority"] = "http://127.0.0.1:8080",
            ["IdentityService:Issuer"] = "http://127.0.0.1:8080",
            ["IdentityService:Audience"] = "lexarbor",
            ["IdentityService:RequireHttpsMetadata"] = "false",
            ["AdminAuthentication:RequiredRole"] = "admin",
            ["AdminAuthentication:OidcCode:ClientId"] = "vocabulary-client",
            ["AdminAuthentication:OidcCode:ClientSecret"] = "vocabulary-client-secret",
            [Redirect] = "https://admin.example.test/admin/auth/callback"
        }).AddInMemoryCollection(values).Build();

    [Fact]
    public void TheDeploymentDefaults_AreValid()
    {
        Assert.Empty(Invalid(null));
        Assert.Empty(Invalid(new Dictionary<string, string?>
        {
            ["IdentityService:Authority"] = "https://signacore.example.com",
            ["IdentityService:RequireHttpsMetadata"] = null,
            ["Network:TrustedProxies:0"] = "172.18.0.2",
            ["Network:TrustedProxies:1"] = "[::1]",
            ["Network:TrustedNetworks:0"] = "172.016.0.0/12",
            ["Network:ForwardLimit"] = "2",
            ["Telemetry:Otlp:Traces:Enabled"] = "true",
            [Traces + ":Endpoint"] = "https://collector.example.test:4317",
            [Traces + ":Protocol"] = "httpprotobuf",
            [Traces + ":AuthenticationHeaderName"] = "Authorization",
            ["Logging:LogLevel:Default"] = "None"
        }, authorization: "synthetic-authorization"));
    }

    [Theory]
    [InlineData("not a uri", Authority)]
    [InlineData("https://id.example.com/root", Authority)]
    [InlineData("https://user@id.example.com", Authority)]
    [InlineData("ftp://id.example.com", Authority)]
    public void Authority_MustBeABareHttpOrigin(string authority, string expected)
        => Assert.Equal([expected], Invalid(new() { [Authority] = authority }));

    [Theory]
    [InlineData("IdentityService:Issuer", "", "IdentityService:Issuer")]
    [InlineData("IdentityService:Audience", "two words", "IdentityService:Audience")]
    [InlineData("AdminAuthentication:RequiredRole", " ", "AdminAuthentication:RequiredRole")]
    [InlineData("IdentityService:RequireHttpsMetadata", "true", Authority)]
    [InlineData("IdentityService:RequireHttpsMetadata", "maybe", "IdentityService:RequireHttpsMetadata")]
    public void Trust_MustStayRestrictive(string key, string value, string expected)
        => Assert.Equal([expected], Invalid(new() { [key] = value }));

    [Fact]
    public void AnAbsentTrustKey_KeepsItsOwnDefault()
    {
        // No identity, role, rate-limit, network, logging or telemetry key at all.
        var minimal = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminAuthentication:OidcCode:ClientId"] = "vocabulary-client",
            ["AdminAuthentication:OidcCode:ClientSecret"] = "vocabulary-client-secret"
        }).Build();
        Assert.Empty(new StartupConfigurationValidator(new TestEnvironment(), new AdminOidcSettings()).InvalidFields(minimal));
    }

    [Theory]
    [InlineData("https://admin.example.test/other", Redirect)]
    [InlineData("http://admin.example.test/admin/auth/callback?registered=1", Redirect)]
    [InlineData("/admin/auth/callback", Redirect)]
    [InlineData("https://admin.example.test/admin/auth/logout/return/", "AdminAuthentication:OidcCode:PostLogoutRedirectUri")]
    [InlineData("https://admin.example.test/logout", "AdminAuthentication:OidcCode:PostLogoutRedirectUri")]
    public void HostedLoginUris_MatchTheirFixedRoutes(string value, string expected)
        => Invalid(new() { [expected] = value }).Contains(expected);

    [Theory]
    [InlineData("openid email")]
    [InlineData("openid profile offline_access")]
    public void Scope_IsOpenIdWithOptionalProfile(string scope)
        => Assert.Equal(["AdminAuthentication:OidcCode:Scope"],
            Invalid(new() { ["AdminAuthentication:OidcCode:Scope"] = scope }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("openid")]
    [InlineData(" profile openid ")]
    public void AnAbsentScope_IsTheClientsDefault(string? scope)
        => Assert.Empty(Invalid(new() { ["AdminAuthentication:OidcCode:Scope"] = scope }));

    [Fact]
    public void ScopeNames_AreCaseSensitive()
        => Assert.Equal(["AdminAuthentication:OidcCode:Scope"],
            Invalid(new() { ["AdminAuthentication:OidcCode:Scope"] = "OPENID profile" }));

    [Fact]
    public void AWorkingSignIn_CannotBeLeftIncomplete()
    {
        Assert.Contains(ClientSecret, Invalid(new() { [ClientSecret] = "" }));
        Assert.Contains("AdminAuthentication:OidcCode:ClientId", Invalid(new() { ["AdminAuthentication:OidcCode:ClientId"] = "" }));
        Assert.Contains(Redirect, Invalid(new() { [Redirect] = "" }));
    }

    [Fact]
    public void AnUnfinishedSignIn_MayBeCompletedStepByStep()
        => Assert.DoesNotContain(ClientSecret, Invalid(new() { [ClientSecret] = "" }, signInConfigured: false));

    [Theory]
    [InlineData("RateLimits:AdminLogin:PermitLimit", "0", false)]
    [InlineData("RateLimits:AdminLogin:PermitLimit", "0", true)]
    [InlineData("RateLimits:AdminLogin:PermitLimit", "10001", false)]
    [InlineData("RateLimits:AdminLogin:PermitLimit", "10001", true)]
    [InlineData("RateLimits:AdminLogin:PermitLimit", "many", true)]
    [InlineData("RateLimits:AdminLogin:WindowSeconds", "9", false)]
    [InlineData("RateLimits:AdminLogin:WindowSeconds", "9", true)]
    [InlineData("RateLimits:AdminLogin:WindowSeconds", "601", true)]
    public void EnabledPolicies_StayInsideTheLimiterBounds(string key, string value, bool enabled)
    {
        var policy = key[..key.LastIndexOf(':')];
        var invalid = Invalid(new() { [key] = value, [policy + ":Enabled"] = enabled ? "true" : "false" });
        // Only the offending number is named, and like startup a disabled policy may carry
        // numbers the limiter would not accept.
        Assert.Equal(enabled || value == "many" ? new[] { key } : Array.Empty<string>(), invalid);
        if (key != policy + ":WindowSeconds") Assert.DoesNotContain(policy + ":WindowSeconds", invalid);
    }

    [Theory]
    [InlineData("Database:SlowCommandThresholdMilliseconds", "500")]
    [InlineData("Database:SlowCommandThresholdMilliseconds", "0")]
    [InlineData("Database:InitializeOnStartup", "false")]
    [InlineData("Database:LegacySqlitePath", "")]
    [InlineData("Database:LegacySqlitePath", "data/nowhere/vocabulary.db")]
    [InlineData("Database:LegacySqlitePath", ".")]
    public void Database_Keys_AcceptWhatStartupAccepts(string key, string value)
        => Assert.Empty(Invalid(new() { [key] = value }));

    [Theory]
    [InlineData("Database:SlowCommandThresholdMilliseconds", "-1")]
    [InlineData("Database:SlowCommandThresholdMilliseconds", "soon")]
    [InlineData("Database:SlowCommandThresholdMilliseconds", " 500")]
    [InlineData("Database:SlowCommandThresholdMilliseconds", "99999999999")]
    [InlineData("Database:InitializeOnStartup", "maybe")]
    public void Database_Keys_RefuseWhatStartupRefuses(string key, string value)
        => Assert.Equal([key], Invalid(new() { [key] = value }));

    [Fact]
    public void ALegacyDatabaseWhereTheStartLooks_WouldStopTheNextStart()
    {
        // A file that exists, relative to the content root: what an operator who upgraded
        // without moving the old database away has.
        var present = Path.GetFileName(typeof(StartupConfigurationValidator).Assembly.Location);
        Assert.Equal(["Database:LegacySqlitePath"], Invalid(new() { ["Database:LegacySqlitePath"] = present }));
    }

    [Theory]
    [InlineData("VocabularyLearning:SlowAnswerMs", "0")]
    [InlineData("VocabularyLearning:FillLettersSlowAnswerMs", "0")]
    [InlineData("VocabularyLearning:QuestionExpiryMinutes", "0")]
    [InlineData("VocabularyLearning:TimeZone", "Mars/Olympus")]
    [InlineData("VocabularyLearning:Fsrs:DesiredRetention", "0")]
    [InlineData("VocabularyLearning:Fsrs:DesiredRetention", "1")]
    [InlineData("VocabularyLearning:Fsrs:FamiliarThresholdDays", "0")]
    [InlineData("VocabularyLearning:Fsrs:MasteredThresholdDays", "3")]
    [InlineData("VocabularyLearning:Fsrs:MaximumIntervalDays", "0")]
    [InlineData("VocabularyLearning:ErrorProne:WindowDays", "0")]
    [InlineData("VocabularyLearning:ErrorProne:WindowDays", "3651")]
    [InlineData("VocabularyLearning:ErrorProne:MinimumAttempts", "0")]
    [InlineData("VocabularyLearning:ErrorProne:ErrorRateThreshold", "1.5")]
    [InlineData("VocabularyLearning:ErrorProne:MinimumForgetCount", "0")]
    public void Learning_FollowsItsOwnOptionsRules(string key, string value)
        => Assert.Equal([key], Invalid(new() { [key] = value }));

    [Theory]
    [InlineData("VocabularyLearning:Fsrs:LearningStepsMinutes:1")]
    [InlineData("VocabularyLearning:Fsrs:RelearningStepsMinutes:0")]
    public void Learning_AStepThatIsNotPositive_NamesItsList(string key)
        => Assert.Equal([key[..key.LastIndexOf(':')]], Invalid(new() { [key] = "0" }));

    [Fact]
    public void Learning_AStepTheBinderCannotRead_IsDroppedAtStartupToo()
        // The binder leaves out a step it cannot read as a number, and the start goes on: nothing is refused.
        => Assert.Empty(Invalid(new() { ["VocabularyLearning:Fsrs:LearningStepsMinutes:0"] = "one" }));

    [Theory]
    [InlineData("VocabularyLearning:SlowAnswerMs", "soon", "VocabularyLearning:SlowAnswerMs")]
    [InlineData("VocabularyLearning:SlowAnswerMs", "99999999999", "VocabularyLearning:SlowAnswerMs")]
    [InlineData("VocabularyLearning:Fsrs:DesiredRetention", "most", "VocabularyLearning:Fsrs:DesiredRetention")]
    public void Learning_TextANumberCannotBeReadFrom_NamesThatKey(string key, string value, string expected)
        => Assert.Equal([expected], Invalid(new() { [key] = value }));

    [Fact]
    public void Learning_AStepBeyondWhatAWholeNumberHolds_IsDroppedAtStartupToo()
        // The binder leaves such a step out without failing the start, so nothing is refused here;
        // the key's own bound is what keeps it out of a settings request.
        => Assert.Empty(Invalid(new() { ["VocabularyLearning:Fsrs:LearningStepsMinutes:0"] = "99999999999" }));

    [Theory]
    [InlineData("VocabularyLearning:SlowAnswerMs", "8000")]
    [InlineData("VocabularyLearning:QuestionExpiryMinutes", "45")]
    [InlineData("VocabularyLearning:TimeZone", "Asia/Shanghai")]
    [InlineData("VocabularyLearning:Fsrs:DesiredRetention", "0.85")]
    [InlineData("VocabularyLearning:Fsrs:LearningStepsMinutes:0", "2")]
    [InlineData("VocabularyLearning:Fsrs:LearningStepsMinutes:1", "20")]
    [InlineData("VocabularyLearning:ErrorProne:WindowDays", "60")]
    public void Learning_AcceptedValues_AreValid(string key, string value)
        => Assert.Empty(Invalid(new() { [key] = value }));

    [Theory]
    [InlineData("https://lexarbor.example.test", "false", true)]
    [InlineData("https://lexarbor.example.test/", "false", true)]
    [InlineData("http://lexarbor.example.test", "true", true)]
    [InlineData("http://lexarbor.example.test", "false", false)]
    [InlineData("", "false", false)]
    [InlineData("https://user@lexarbor.example.test", "false", false)]
    public void ALearningConnection_SendsItsKeysOnlyToAnAllowedCatalog(string baseUrl, string allowInsecure, bool accepted)
    {
        var values = new Dictionary<string, string?>
        {
            ["Lexarbor:BaseUrl"] = baseUrl,
            ["Lexarbor:AllowInsecureHttp"] = allowInsecure,
            ["Lexarbor:ApiKey"] = "synthetic-catalog-key",
            ["Lexarbor:Integration:ApiKey"] = "synthetic-submission-key",
            ["Lexarbor:SupplementaryBookId"] = "supplementary-book"
        };
        var invalid = Invalid(values);
        if (accepted) Assert.Empty(invalid);
        else Assert.Contains("Lexarbor:BaseUrl", invalid);
    }

    [Theory]
    [InlineData("Lexarbor:ApiKey", "synthetic,key")]
    [InlineData("Lexarbor:ApiKey", "synthetic key")]
    [InlineData("Lexarbor:ApiKey", "密钥")]
    [InlineData("Lexarbor:Integration:ApiKey", "synthetic,key")]
    public void ALearningConnection_KeyIsPrintableAndCarriesNoComma(string key, string value)
        => Assert.Contains(key, Invalid(new() { ["Lexarbor:BaseUrl"] = "https://lexarbor.example.test", [key] = value }));

    [Fact]
    public void ALearningConnection_SwitchMustBind()
        => Assert.Equal(["Lexarbor:AllowInsecureHttp"], Invalid(new() { ["Lexarbor:AllowInsecureHttp"] = "maybe" }));

    [Theory]
    [InlineData("Network:ForwardLimit", "11", "Network:ForwardLimit")]
    [InlineData("Network:ForwardLimit", "0", "Network:ForwardLimit")]
    [InlineData("Network:TrustedProxies:0", "localhost", "Network:TrustedProxies")]
    [InlineData("Network:TrustedProxies:0", "10.0.0.1/24", "Network:TrustedProxies")]
    [InlineData("Network:TrustedNetworks:0", "10.0.0.1", "Network:TrustedNetworks")]
    [InlineData("Network:TrustedNetworks:0", "172.18.0.0/33", "Network:TrustedNetworks")]
    public void Forwarding_RejectsWhatTheTrustBoundaryRejects(string key, string value, string expected)
        => Assert.Equal([expected], Invalid(new() { [key] = value }));

    [Fact]
    public void Forwarding_RequiresATrustedHopAndDistinctEntries()
    {
        Assert.Equal(["Network:TrustedProxies", "Network:TrustedNetworks"], Invalid(new() { ["Network:ForwardLimit"] = "2" }));
        Assert.Equal(["Network:TrustedProxies"], Invalid(new()
        {
            ["Network:TrustedProxies:0"] = "10.0.0.1",
            ["Network:TrustedProxies:1"] = "10.0.0.1",
            ["Network:ForwardLimit"] = "2"
        }));
        Assert.Empty(Invalid(new()
        {
            ["Network:TrustedProxies:0"] = "10.0.0.1",
            ["Network:TrustedProxies:1"] = "2001:DB8::10",
            ["Network:ForwardLimit"] = "2"
        }));
    }

    [Theory]
    [InlineData("Audio:RecoveryLeaseSeconds", "60")]
    [InlineData("Audio:RecoveryMaxRetrySeconds", "30")]
    public void AudioLimits_HoldEvenWhileStorageIsDisabled(string key, string value)
        => Assert.Equal(Advanced, Invalid(new() { [key] = value }));

    [Theory]
    [InlineData("Audio:PlaybackTtlSeconds", 59)]
    [InlineData("Audio:PlaybackTtlSeconds", 3601)]
    [InlineData("Audio:RecoveryRecheckSeconds", 604801)]
    [InlineData("Audio:MaxUploadBytes", 20971521)]
    [InlineData("RateLimits:AdminLogin:WindowSeconds", 601)]
    [InlineData("Network:ForwardLimit", 11)]
    [InlineData("VocabularyLearning:ErrorProne:WindowDays", 3651)]
    [InlineData("Database:SlowCommandThresholdMilliseconds", -1)]
    // The whole number the binder reads a key as is the ceiling of a key with no smaller one.
    [InlineData("Database:SlowCommandThresholdMilliseconds", 2147483648)]
    [InlineData("VocabularyLearning:SlowAnswerMs", 2147483648)]
    public void RestartValues_AreBoundedPerField(string key, long value)
        => Assert.False(ConfigurationCatalog.Find(key)!.Valid(JsonValue.Create(value)));

    [Fact]
    public void RestartValues_AcceptTheirOwnTypeOnly()
    {
        // A null removes a key so the next start reads the default; numbers and booleans cannot.
        Assert.True(ConfigurationCatalog.Find("IdentityService:RequireHttpsMetadata")!.Valid(null));
        Assert.True(ConfigurationCatalog.Find("Network:TrustedProxies")!.Valid(new JsonArray("10.0.0.1", "[::1]")));
        Assert.True(ConfigurationCatalog.Find("Logging:LogLevel:Default")!.Valid(JsonValue.Create("Debug")));
        Assert.True(ConfigurationCatalog.Find("AdminAuthentication:OidcCode:ClientSecret")!.Valid(JsonValue.Create("synthetic-secret")));
        Assert.False(ConfigurationCatalog.Find("Audio:MaxQueuedUploads")!.Valid(null));
        Assert.False(ConfigurationCatalog.Find("Audio:MaxQueuedUploads")!.Valid(JsonValue.Create("8")));
        Assert.False(ConfigurationCatalog.Find("Audio:MaxQueuedUploads")!.Valid(JsonValue.Create(8.5)));
        Assert.False(ConfigurationCatalog.Find("Network:TrustedProxies")!.Valid(JsonValue.Create("10.0.0.1")));
        Assert.False(ConfigurationCatalog.Find("Network:TrustedProxies")!.Valid(new JsonArray(1)));
        Assert.False(ConfigurationCatalog.Find("Network:TrustedProxies")!.Valid(new JsonArray("")));
        Assert.False(ConfigurationCatalog.Find("Database:InitializeOnStartup")!.Valid(JsonValue.Create("true")));
        Assert.False(ConfigurationCatalog.Find("AdminAuthentication:OidcCode:ClientSecret")!.Valid(JsonValue.Create("line\u007Fbreak")));
        // A decimal keeps its fraction; a list of steps is numbers, at least one, and a file
        // cannot hold an empty one.
        var retention = ConfigurationCatalog.Find("VocabularyLearning:Fsrs:DesiredRetention")!;
        Assert.True(retention.Valid(JsonValue.Create(0.85m)));
        Assert.False(retention.Valid(JsonValue.Create("0.85")));
        var steps = ConfigurationCatalog.Find("VocabularyLearning:Fsrs:LearningStepsMinutes")!;
        Assert.True(steps.Valid(new JsonArray(1, 10)));
        Assert.False(steps.Valid(new JsonArray()));
        Assert.False(steps.Valid(new JsonArray(1, "2")));
        Assert.False(steps.Valid(new JsonArray(0)));
        Assert.False(steps.Valid(JsonValue.Create("1,10")));
        Assert.True(ConfigurationCatalog.Find("VocabularyLearning:TimeZone")!.Valid(null));
    }

    [Theory]
    [InlineData("Logging:LogLevel:Default", "Chatty")]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "verbose")]
    [InlineData(Traces + ":Protocol", "Thrift")]
    [InlineData(Traces + ":Endpoint", "collector:4317")]
    [InlineData(Traces + ":Enabled", "yes")]
    [InlineData(Traces + ":AuthenticationHeaderName", "bad header")]
    [InlineData("Service:InstanceId", "bad\u0007id")]
    public void Values_MatchTheirKinds(string key, string value)
        => Assert.Equal([key], Invalid(new() { [key] = value }));

    [Fact]
    public void AnAuthenticatedExporter_NeedsItsValueFromTheEnvironment()
    {
        var enabled = new Dictionary<string, string?> { [Traces + ":Enabled"] = "true", [Traces + ":AuthenticationHeaderName"] = "Authorization" };
        Assert.Equal([Traces + ":AuthenticationHeaderName"], Invalid(enabled));
        Assert.Equal([Traces + ":AuthenticationHeaderName"], Invalid(enabled, authorization: " "));
        Assert.Empty(Invalid(enabled, authorization: "synthetic-authorization"));
        // A disabled signal never authenticates, so a name without a value is harmless.
        Assert.Empty(Invalid(new() { [Traces + ":AuthenticationHeaderName"] = "Authorization" }));
    }

    [Fact]
    public void AnOverlongInstanceId_IsRefused()
        => Assert.Equal(["Service:InstanceId"], Invalid(new() { ["Service:InstanceId"] = new string('a', 257) }));
}
