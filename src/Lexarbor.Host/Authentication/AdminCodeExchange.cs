using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Host.Authentication;

public sealed class OidcCodeOptions
{
    public const string SectionName = "AdminAuthentication:OidcCode";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string Scope { get; set; } = "openid profile";
}

public enum AdminCodeStatus { Success, Invalid, Forbidden, Unavailable }

// Classes intentionally have no generated ToString that could disclose tokens.
public sealed class AdminCodeResult
{
    public AdminCodeStatus Status { get; }
    public string? AccessToken { get; }
    public string? IdToken { get; }
    public ClaimsPrincipal? Principal { get; }
    private AdminCodeResult(AdminCodeStatus status, string? access = null, string? id = null, ClaimsPrincipal? principal = null)
        => (Status, AccessToken, IdToken, Principal) = (status, access, id, principal);
    internal static AdminCodeResult Failed(AdminCodeStatus status) => new(status);
    internal static AdminCodeResult Succeeded(string access, string id, ClaimsPrincipal principal)
        => new(AdminCodeStatus.Success, access, id, principal);
}

public sealed class AdminCodeMetadata
{
    public AdminCodeStatus Status { get; }
    public string? AuthorizationEndpoint { get; }
    public bool InvalidConfiguration { get; }
    internal OpenIdConnectConfiguration? Configuration { get; }
    internal AdminCodeMetadata(AdminCodeStatus status, OpenIdConnectConfiguration? configuration = null, bool invalidConfiguration = false)
        => (Status, AuthorizationEndpoint, Configuration, InvalidConfiguration) = (status, configuration?.AuthorizationEndpoint, configuration, invalidConfiguration);
}

/// <summary>
/// Internal only. Callers must first validate and consume their browser-bound pending
/// transaction. Never retry code redemption; only a received JWT may be revalidated.
/// This service neither signs in nor enables a public login mode.
/// </summary>
public sealed class AdminCodeExchange(
    IHttpClientFactory clients, IOptions<OidcCodeOptions> codeOptions,
    IOptions<IdentityServiceOptions> identityOptions, IOptions<AdminAuthenticationOptions> adminOptions,
    IOptionsMonitor<JwtBearerOptions> bearerOptions, IHostEnvironment environment, TimeProvider clock)
{
    public const string BackchannelName = "LexarborCodeExchange";
    public const int MaximumResponseBytes = 64 * 1024;

    public async Task<AdminCodeMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured) return new(AdminCodeStatus.Unavailable, invalidConfiguration: true);
        try
        {
            var manager = bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme).ConfigurationManager;
            if (manager is null) return new(AdminCodeStatus.Unavailable);
            var configuration = await manager.GetConfigurationAsync(cancellationToken);
            return TrustedMetadata(configuration)
                ? new(AdminCodeStatus.Success, configuration)
                : new(AdminCodeStatus.Unavailable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new(AdminCodeStatus.Unavailable);
        }
    }

    public async Task<AdminCodeResult> RedeemAsync(string code, string verifier, string nonce,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Ascii(code, 1, 500) || !ProtocolValue(verifier, 43, 128) || !ProtocolValue(nonce, 22, 128))
            return AdminCodeResult.Failed(AdminCodeStatus.Invalid);
        var metadata = await GetMetadataAsync(cancellationToken);
        if (metadata.Configuration is null) return AdminCodeResult.Failed(AdminCodeStatus.Unavailable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var settings = codeOptions.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, metadata.Configuration.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = settings.RedirectUri,
                    ["code_verifier"] = verifier,
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret
                })
            };
            using var response = await clients.CreateClient(BackchannelName).SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token);
            if (bytes is null) return AdminCodeResult.Failed(AdminCodeStatus.Unavailable);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
                return AdminCodeResult.Failed(AdminCodeStatus.Unavailable);
            if (!response.IsSuccessStatusCode)
                return AdminCodeResult.Failed(response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized
                    && String(root, "error") == "invalid_grant" ? AdminCodeStatus.Invalid : AdminCodeStatus.Unavailable);
            var access = String(root, "access_token");
            var id = String(root, "id_token");
            if (!Ascii(access, 1, 8192) || !Ascii(id, 1, 8192) || String(root, "token_type") != "Bearer"
                || !Integer(root, "expires_in", out var expiry) || expiry <= 0 || expiry > int.MaxValue
                || !ValidScope(String(root, "scope"))
                || String(root, "scope")!.Split(' ').Except(settings.Scope.Split(' '), StringComparer.Ordinal).Any())
                return AdminCodeResult.Failed(AdminCodeStatus.Invalid);
            var configuration = metadata.Configuration;
            var accessResult = await ValidateAsync(access!, "at+jwt", identityOptions.Value.Audience, null, configuration);
            var idResult = await ValidateAsync(id!, "JWT", settings.ClientId, nonce, configuration);
            var options = bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
            if ((accessResult?.Exception is SecurityTokenSignatureKeyNotFoundException || idResult?.Exception is SecurityTokenSignatureKeyNotFoundException)
                && options.RefreshOnIssuerKeyNotFound && options.ConfigurationManager is not null)
            {
                options.ConfigurationManager.RequestRefresh();
                var refreshed = await GetMetadataAsync(timeout.Token);
                if (refreshed.Configuration is null) return AdminCodeResult.Failed(AdminCodeStatus.Unavailable);
                accessResult = await ValidateAsync(access!, "at+jwt", identityOptions.Value.Audience, null, refreshed.Configuration);
                idResult = await ValidateAsync(id!, "JWT", settings.ClientId, nonce, refreshed.Configuration);
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (accessResult?.IsValid != true || idResult?.IsValid != true || accessResult.ClaimsIdentity is null
                || accessResult.ClaimsIdentity.FindFirst("sub")?.Value != idResult.ClaimsIdentity?.FindFirst("sub")?.Value
                || !long.TryParse(accessResult.ClaimsIdentity.FindFirst("exp")?.Value, out var exactExpiry)
                || exactExpiry <= clock.GetUtcNow().ToUnixTimeSeconds())
                return AdminCodeResult.Failed(AdminCodeStatus.Invalid);
            var principal = new ClaimsPrincipal(accessResult.ClaimsIdentity);
            return VocabularyClaims.HasRole(principal, adminOptions.Value.RequiredRole)
                ? AdminCodeResult.Succeeded(access!, id!, principal)
                : AdminCodeResult.Failed(AdminCodeStatus.Forbidden);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // No raw exceptions, HTTP forms or response bodies enter logs/results.
            return AdminCodeResult.Failed(AdminCodeStatus.Unavailable);
        }
    }

    private async Task<TokenValidationResult?> ValidateAsync(string token, string type, string audience, string? nonce,
        OpenIdConnectConfiguration configuration)
    {
        var segments = token.Split('.');
        if (segments.Length != 3) return null;
        using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[0]));
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[1]));
        var h = header.RootElement;
        var p = payload.RootElement;
        if (h.ValueKind != JsonValueKind.Object || p.ValueKind != JsonValueKind.Object
            || h.EnumerateObject().GroupBy(v => v.Name).Any(g => g.Count() != 1)
            || p.EnumerateObject().GroupBy(v => v.Name).Any(g => g.Count() != 1)
            || String(h, "alg") != "RS256" || String(h, "typ") != type || string.IsNullOrWhiteSpace(String(h, "kid"))
            || String(p, "iss") != identityOptions.Value.Issuer || !SingleAudience(p, audience)
            || string.IsNullOrWhiteSpace(String(p, "sub")) || (nonce is not null && String(p, "nonce") != nonce)
            || !Integer(p, "iat", out var issued) || !Integer(p, "exp", out var expires)
            || issued > clock.GetUtcNow().ToUnixTimeSeconds() || expires <= issued
            || expires <= clock.GetUtcNow().ToUnixTimeSeconds()) return null;
        var kid = String(h, "kid");
        var keys = configuration.SigningKeys.Where(k => k.KeyId == kid && (k is RsaSecurityKey || k is JsonWebKey { Kty: "RSA" })).ToArray();
        if (keys.Length == 0) return new TokenValidationResult { Exception = new SecurityTokenSignatureKeyNotFoundException() };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            IssuerSigningKeys = keys,
            TryAllIssuerSigningKeys = false,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidIssuer = identityOptions.Value.Issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidTypes = [type],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expiration, _, _) => expiration.HasValue
                && new DateTimeOffset(expiration.Value, TimeSpan.Zero) > clock.GetUtcNow()
                && (!notBefore.HasValue || new DateTimeOffset(notBefore.Value, TimeSpan.Zero) <= clock.GetUtcNow().AddSeconds(nonce is null ? 0 : 30)),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
        return await new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 8192 }.ValidateTokenAsync(token, parameters);
    }

    public bool IsConfigured => ValidConfiguration();

    private bool ValidConfiguration()
    {
        var settings = codeOptions.Value;
        return Ascii(settings.ClientId, 1, 500) && !string.IsNullOrWhiteSpace(settings.ClientId)
            && Ascii(settings.ClientSecret, 1, 500) && !string.IsNullOrWhiteSpace(settings.ClientSecret)
            && settings.ClientId == identityOptions.Value.Audience && ValidScope(settings.Scope)
            && Ascii(settings.RedirectUri, 1, 500) && SafeUri(settings.RedirectUri, out var redirect)
            && redirect.AbsolutePath == "/admin/auth/callback" && ValidRedirectQuery(redirect)
            && (redirect.Scheme == "https" || IsLocalEnvironment() && redirect.Scheme == "http"
                && redirect.Host is "127.0.0.1" or "[::1]");
    }

    private static bool ValidRedirectQuery(Uri redirect)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "state", "iss", "code", "error", "error_description" };
        var query = QueryHelpers.ParseQuery(redirect.Query);
        return query.All(pair => !reserved.Contains(pair.Key) && pair.Value.Count == 1);
    }

    private bool TrustedMetadata(OpenIdConnectConfiguration configuration)
    {
        if (configuration.Issuer != identityOptions.Value.Issuer || !SafeUri(configuration.Issuer, out var issuer)
            || issuer.Scheme != "https" && !(IsLocalEnvironment() && issuer.Scheme == "http")) return false;
        return TrustedEndpoint(configuration.AuthorizationEndpoint, issuer, false)
            && TrustedEndpoint(configuration.TokenEndpoint, issuer, false)
            && TrustedEndpoint(configuration.JwksUri, issuer, true);
    }
    private static bool TrustedEndpoint(string value, Uri issuer, bool allowQuery)
        => SafeUri(value, out var uri) && uri.Scheme == issuer.Scheme && uri.Host == issuer.Host && uri.Port == issuer.Port
            && (allowQuery || uri.Query.Length == 0);
    private bool IsLocalEnvironment() => environment.IsDevelopment() || environment.IsEnvironment("Testing");
    private static bool SafeUri(string value, out Uri uri)
    {
        uri = null!;
        return Ascii(value, 1, 500) && !value.Contains('\\') && Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Host.Length > 0;
    }
    private static bool ValidScope(string? scope)
        => scope is not null && scope.Split(' ') is var parts && parts.Contains("openid")
            && parts.All(p => p is "openid" or "profile") && parts.Distinct(StringComparer.Ordinal).Count() == parts.Length;
    private static bool Ascii(string? value, int min, int max)
        => value is not null && value.Length >= min && value.Length <= max && value.All(c => c is >= '!' and <= '~');
    private static bool ProtocolValue(string value, int min, int max)
        => Ascii(value, min, max) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~');
    private static string? String(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool Integer(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out number);
    }
    private static bool SingleAudience(JsonElement value, string audience)
        => value.TryGetProperty("aud", out var property) && (property.ValueKind == JsonValueKind.String && property.GetString() == audience
            || property.ValueKind == JsonValueKind.Array && property.GetArrayLength() == 1
                && property[0].ValueKind == JsonValueKind.String && property[0].GetString() == audience);
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return bytes.ToArray();
            if (bytes.Length + read > MaximumResponseBytes) return null;
            bytes.Write(buffer, 0, read);
        }
    }
}
