using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Microsoft.Extensions.Options;
using ServiceMantle;

namespace Lexarbor.Host.Configuration;

/// <summary>
/// Checks a next-start candidate the way startup would, so the administration surface cannot
/// persist a configuration the host would refuse to start with — or one that would sign every
/// administrator out. The answer names keys only, never values: values may be secrets.
/// </summary>
internal sealed partial class StartupConfigurationValidator(IHostEnvironment environment, AdminOidcSettings startupLogin,
    Func<string?>? authorizationValue = null)
{
    private const string Authority = "IdentityService:Authority";
    private const string Issuer = "IdentityService:Issuer";
    private const string Audience = "IdentityService:Audience";
    private const string ClientId = "AdminAuthentication:OidcCode:ClientId";
    private const string ClientSecret = "AdminAuthentication:OidcCode:ClientSecret";
    private const string RedirectUri = "AdminAuthentication:OidcCode:RedirectUri";
    private const string PostLogoutRedirectUri = "AdminAuthentication:OidcCode:PostLogoutRedirectUri";
    private const string Scope = "AdminAuthentication:OidcCode:Scope";
    private const string SlowCommandThreshold = "Database:SlowCommandThresholdMilliseconds";
    private const string InitializeOnStartup = "Database:InitializeOnStartup";
    private const string LegacySqlitePath = "Database:LegacySqlitePath";
    private const string CatalogBaseUrl = "Lexarbor:BaseUrl";
    private const string CatalogApiKey = "Lexarbor:ApiKey";
    private const string AllowInsecureHttp = "Lexarbor:AllowInsecureHttp";
    private const string SubmissionApiKey = "Lexarbor:Integration:ApiKey";
    private static readonly string[] ClientKeys = [ClientId, ClientSecret, RedirectUri];
    private static readonly string[] LogKeys =
    [
        "Logging:LogLevel:Default", "Logging:LogLevel:Microsoft.AspNetCore",
        "Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"
    ];
    private static readonly string[] AudioAdvancedKeys = ConfigurationCatalog.RestartFields
        .Where(field => field.Group == "audio-storage").Select(field => field.Key).ToArray();
    private static readonly string[] LearningKeys = ConfigurationCatalog.RestartFields
        .Where(field => field.Group is "learning" or "learning-connection").Select(field => field.Key).ToArray();

    private readonly List<string> _invalid = [];

    public IReadOnlyList<string> InvalidFields(IConfiguration candidate)
    {
        Trust(candidate);
        HostedLogin(candidate);
        RateLimits(candidate);
        Forwarding(candidate);
        Database(candidate);
        Audio(candidate);
        Learning(candidate);
        LearningConnection(candidate);
        Logging(candidate);
        Telemetry(candidate);
        if (candidate["Service:InstanceId"] is { } instance && instance.Length > 0
            && !InstanceId.TryParse(instance, out _))
        {
            _invalid.Add("Service:InstanceId");
        }
        return _invalid.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void Require(bool valid, params string[] keys)
    {
        if (!valid) _invalid.AddRange(keys);
    }

    // A blank issuer or audience would turn token validation off rather than restrict it.
    private void Trust(IConfiguration candidate)
    {
        var identity = candidate.GetSection(IdentityServiceOptions.SectionName);
        // An absent key keeps the option's own default, exactly as startup binds it.
        var defaults = new IdentityServiceOptions();
        var authority = identity["Authority"]?.Trim() ?? defaults.Authority;
        Require(IsAbsoluteUri(authority, allowPath: false) && NoWhitespace(authority), Authority);
        Require(NoWhitespace(identity["Issuer"] ?? defaults.Issuer), Issuer);
        Require(NoWhitespace(identity["Audience"] ?? defaults.Audience), Audience);
        bool? configured = bool.TryParse(identity["RequireHttpsMetadata"], out var flag) ? flag : null;
        // Text the binder cannot read as the switch's value stops the start.
        Require(identity["RequireHttpsMetadata"] is null || configured is not null, "IdentityService:RequireHttpsMetadata");
        var httpsRequired = IdentityServiceOptions.RequiresHttpsMetadata(authority, configured, environment);
        Require(!httpsRequired || authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase), Authority);
        Require(NoWhitespace(candidate["AdminAuthentication:RequiredRole"] ?? new AdminAuthenticationOptions().RequiredRole),
            "AdminAuthentication:RequiredRole");
    }

    private void HostedLogin(IConfiguration candidate)
    {
        var code = candidate.GetSection(OidcCodeOptions.SectionName);
        var complete = NoWhitespace(code["ClientId"]) && NoWhitespace(code["ClientSecret"]) && NoWhitespace(code["RedirectUri"]);
        // A deployment whose administrators can sign in now must still be able to after the restart.
        Require(!startupLogin.Available || complete, ClientKeys);
        var redirect = code["RedirectUri"]?.Trim() ?? "";
        var postLogout = code["PostLogoutRedirectUri"]?.Trim() ?? "";
        Require(redirect.Length == 0 || HasExactPath(redirect, AdminOidcSettings.CallbackPath), RedirectUri);
        Require(postLogout.Length == 0 || HasExactPath(postLogout, AdminOidcSettings.LogoutReturnPath), PostLogoutRedirectUri);
        // An absent scope is the client's own default, `openid profile`.
        var scopes = (string.IsNullOrWhiteSpace(code["Scope"]) ? "openid profile" : code["Scope"]!)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(scopes.Contains("openid") && scopes.All(scope => scope is "openid" or "profile"), Scope);
        try
        {
            // The startup rules themselves, so the two can never drift apart unnoticed.
            new AdminOidcSettings().Load(candidate);
        }
        catch (Exception exception) when (exception is OptionsValidationException or InvalidOperationException)
        {
            // Name what startup named: a refused rule by its message, a value the binder could
            // not read by the path in its message. A failure none of its keys explains is drift
            // between the two rule sets: refuse the whole sign-in section rather than let it through.
            var failures = exception is OptionsValidationException validation ? validation.Failures.ToArray() : [exception.Message];
            var named = new[] { Authority, Issuer, Audience, "IdentityService:RequireHttpsMetadata", ClientId, ClientSecret, RedirectUri, PostLogoutRedirectUri }
                .Where(key => failures.Any(failure => failure.StartsWith(key + " ", StringComparison.Ordinal)
                    || failure.Contains("'" + key + "'", StringComparison.Ordinal))).ToArray();
            _invalid.AddRange(named.Length > 0 ? named : [.. ClientKeys, Authority, PostLogoutRedirectUri]);
        }
    }

    private void RateLimits(IConfiguration candidate)
    {
        var section = candidate.GetSection(RateLimitOptions.SectionName + ":AdminLogin");
        var defaults = new RateLimitOptions().AdminLogin;
        // Startup binds the numbers before the enabled check: text it cannot parse stops
        // it, and an enabled policy must stay within the limiter's bounds. An absent
        // number keeps its default.
        var enabled = bool.TryParse(section["Enabled"], out var on) && on;
        var permitsValid = Number(section["PermitLimit"], defaults.PermitLimit, out var permits)
            && (!enabled || RateLimitingExtensions.ValidNumbers(new() { PermitLimit = permits, WindowSeconds = 10 }));
        var windowValid = Number(section["WindowSeconds"], defaults.WindowSeconds, out var window)
            && (!enabled || RateLimitingExtensions.ValidNumbers(new() { PermitLimit = 1, WindowSeconds = window }));
        Require(permitsValid, section.Path + ":PermitLimit");
        Require(windowValid, section.Path + ":WindowSeconds");
    }

    // An absent number is the default; only text that is present and cannot be parsed is invalid.
    private static bool Number(string? text, int fallback, out int value)
    {
        value = fallback;
        return text is null || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // The startup rules of the database keys: a threshold that is a whole number of milliseconds,
    // a switch that binds, and no database left by a release before PostgreSQL where the start
    // looks for one.
    private void Database(IConfiguration candidate)
    {
        Require(candidate[SlowCommandThreshold] is null
            || int.TryParse(candidate[SlowCommandThreshold], NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) && threshold >= 0,
            SlowCommandThreshold);
        Require(candidate[InitializeOnStartup] is null || bool.TryParse(candidate[InitializeOnStartup], out _), InitializeOnStartup);
        var path = candidate[LegacySqlitePath];
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var attributes = File.GetAttributes(Path.GetFullPath(path, environment.ContentRootPath));
            // A file there, in a deployment that never imported one, is what the start refuses over.
            if ((attributes & FileAttributes.Directory) == 0) _invalid.Add(LegacySqlitePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _invalid.Add(LegacySqlitePath);
        }
    }

    // The options' own startup rules, so the two can never drift apart unnoticed.
    private void Learning(IConfiguration candidate)
    {
        var learning = new VocabularyLearningOptions();
        var scheduling = new VocabularyFsrsOptions();
        var errorProne = new VocabularyErrorProneOptions();
        try
        {
            candidate.GetSection(VocabularyLearningOptions.SectionName).Bind(learning);
            candidate.GetSection(VocabularyFsrsOptions.SectionName).Bind(scheduling);
            candidate.GetSection(VocabularyErrorProneOptions.SectionName).Bind(errorProne);
        }
        catch (InvalidOperationException exception)
        {
            // Text a number cannot be read from. The binder's message carries the path it failed
            // at, relative to whichever section it was binding: name the key that path ends.
            bool Names(string key)
            {
                var segments = key.Split(':');
                return Enumerable.Range(0, segments.Length).Any(skip => exception.Message.Contains("'" + string.Join(':', segments.Skip(skip)), StringComparison.Ordinal));
            }
            var named = LearningKeys.Where(Names).ToArray();
            _invalid.AddRange(named.Length > 0 ? named : LearningKeys.Where(key => !key.StartsWith("Lexarbor:", StringComparison.Ordinal)));
            return;
        }
        var failures = VocabularyLearningOptions.Validate(learning).Concat(VocabularyFsrsOptions.Validate(scheduling))
            .Concat(new VocabularyErrorProneOptionsValidation().Validate(null, errorProne).Failures ?? []);
        foreach (var failure in failures)
        {
            var key = LearningKeys.FirstOrDefault(key => failure.StartsWith(key + " ", StringComparison.Ordinal) || failure == key);
            // A failure no key explains is drift between the two rule sets: refuse the group's keys.
            _invalid.AddRange(key is null ? LearningKeys.Where(key => !key.StartsWith("Lexarbor:", StringComparison.Ordinal)) : [key]);
        }
    }

    // The rules the client registrations enforce at startup: a key is a printable value without
    // commas, and it is only ever sent to an HTTPS catalog address.
    private void LearningConnection(IConfiguration candidate)
    {
        var baseUrl = (candidate[CatalogBaseUrl] ?? "").TrimEnd('/');
        var allowInsecure = bool.TryParse(candidate[AllowInsecureHttp], out var insecure) && insecure;
        Require(candidate[AllowInsecureHttp] is null || bool.TryParse(candidate[AllowInsecureHttp], out _), AllowInsecureHttp);
        foreach (var key in new[] { CatalogApiKey, SubmissionApiKey })
        {
            var value = candidate[key] ?? "";
            Require(value.Length == 0 || ApiKeyRules.IsValid(value), key);
            Require(value.Length == 0 || ApiKeyRules.IsAllowedEndpoint(baseUrl, allowInsecure), CatalogBaseUrl, AllowInsecureHttp);
        }
    }

    // The ServiceMantle forwarded-header trust boundary: a limit of 1 to 10 hops, and a trust
    // list with at least one entry whenever forwarding is configured at all.
    private void Forwarding(IConfiguration candidate)
    {
        var proxies = Items(candidate, "Network:TrustedProxies");
        var networks = Items(candidate, "Network:TrustedNetworks");
        var limitValid = Number(candidate["Network:ForwardLimit"], 1, out var limit) && limit is >= 1 and <= 10;
        Require(limitValid, "Network:ForwardLimit");
        var configured = proxies.Length > 0 || networks.Length > 0 || limitValid && limit != 1;
        Require(!configured || proxies.Length > 0 || networks.Length > 0, "Network:TrustedProxies", "Network:TrustedNetworks");

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var proxiesValid = proxies.All(proxy => TryAddress(proxy, out var address) && addresses.Add(address));
        var ranges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var networksValid = networks.All(network => IPNetwork.TryParse(network.Trim(), out var range) && ranges.Add(range!.ToString()));
        Require(proxiesValid, "Network:TrustedProxies");
        Require(networksValid, "Network:TrustedNetworks");
    }

    private void Audio(IConfiguration candidate)
    {
        var audio = new AudioOptions();
        try
        {
            candidate.GetSection(AudioOptions.SectionName).Bind(audio);
        }
        catch (InvalidOperationException)
        {
            _invalid.AddRange(AudioAdvancedKeys);
            return;
        }
        // The live module holds the connection; only the limits can differ here. Their two
        // cross-field rules hold even while audio is disabled, so enabling later cannot be
        // refused over values this surface accepted.
        var valid = audio.RecoveryLeaseSeconds > audio.OperationTimeoutSeconds
            && audio.RecoveryMaxRetrySeconds >= audio.RecoveryRetrySeconds
            && (!audio.Enabled || audio.IsValid());
        Require(valid, AudioAdvancedKeys);
    }

    private void Logging(IConfiguration candidate)
    {
        foreach (var key in LogKeys)
        {
            var text = candidate[key];
            Require(text is null || Enum.TryParse<LogLevel>(text, ignoreCase: true, out _), key);
        }
    }

    private void Telemetry(IConfiguration candidate)
    {
        foreach (var signal in new[] { "Traces", "Metrics" })
        {
            var section = candidate.GetSection("Telemetry:Otlp:" + signal);
            var enabled = bool.TryParse(section["Enabled"], out var on) && on;
            var endpoint = section["Endpoint"]?.Trim() ?? "";
            var protocol = section["Protocol"]?.Trim() ?? "";
            var header = section["AuthenticationHeaderName"]?.Trim() ?? "";
            Require(bool.TryParse(section["Enabled"], out _) || section["Enabled"] is null, section.Path + ":Enabled");
            Require(endpoint.Length == 0 || IsAbsoluteUri(endpoint, allowPath: true), section.Path + ":Endpoint");
            Require(protocol.Length == 0 || Enum.TryParse<ServiceMantle.Diagnostics.Export.Otlp.OtlpProtocol>(protocol, ignoreCase: true, out _),
                section.Path + ":Protocol");
            // No header name means the exporter authenticates with nothing, which is allowed.
            Require(header.Length == 0 || HeaderNamePattern().IsMatch(header), section.Path + ":AuthenticationHeaderName");
            // The exporter fails startup closed when it is told to authenticate without a value.
            var value = (authorizationValue ?? (() => Environment.GetEnvironmentVariable(
                ConfiguredOtlpAuthenticationResolver.ValueEnvironmentVariable)))();
            Require(!(enabled && header.Length > 0) || !string.IsNullOrWhiteSpace(value), section.Path + ":AuthenticationHeaderName");
        }
    }

    private static bool NoWhitespace(string? text) => !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsWhiteSpace);

    private static bool IsAbsoluteUri(string text, bool allowPath)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && (allowPath || uri.AbsolutePath.TrimEnd('/').Length == 0);

    private static bool HasExactPath(string text, string path)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath == path;

    private static string[] Items(IConfiguration candidate, string key)
    {
        var items = new List<string>();
        for (var index = 0; candidate[key + ":" + index] is { } item; index++) items.Add(item);
        return items.ToArray();
    }

    // The trust boundary accepts a bracketed IPv6 literal and compares addresses in their
    // normalized form, so a duplicate written differently is still a duplicate.
    private static bool TryAddress(string text, out string normalized)
    {
        var value = text.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        if (IPAddress.TryParse(value, out var address))
        {
            normalized = address.ToString();
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    [GeneratedRegex("""^[!#$%&'*+\-.^_`|~0-9A-Za-z]{1,256}$""")]
    private static partial Regex HeaderNamePattern();
}
