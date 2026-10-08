using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests.TestInfrastructure;

/// <summary>
/// The synthetic SignaCore authority behind the official client package's backchannel:
/// Discovery, JWKS, the token endpoint and the prepared-logout endpoint, with the defect
/// injection points the hosted-login contract tests use. Registration replaces the
/// <c>SignaCoreHostedLogin</c> HTTP client's primary handler with this stub, so the
/// package's own Discovery client, token client and logout client all speak to it.
/// </summary>
public sealed class SignaCoreAuthorityStub(TimeProvider clock) : HttpMessageHandler
{
    public const string Issuer = "https://issuer.test";
    public const string Redirect = "https://lexarbor.test/admin/auth/callback";
    public const string PostLogoutRedirect = "https://lexarbor.test/admin/auth/logout/return";
    public const string Secret = "synthetic-hosted-secret-marker";
    public const string Kid = "key-1";
    public static readonly string Code = WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes("synthetic-code-marker-0123456789"));
    public static readonly string Handle = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private readonly RSA _rsa = RSA.Create(2048);

    /// <summary>The authority's public signing key, for tests that point the host's
    /// JwtBearer validation parameters at the same key the stub signs with.</summary>
    public RsaSecurityKey SigningKey => new(_rsa.ExportParameters(false)) { KeyId = Kid };

    public bool DiscoveryFails { get; set; }
    /// <summary>Publishes the authorization endpoint on a second origin: the official
    /// same-origin triple check must refuse the whole document.</summary>
    public bool CrossOriginAuthorizationEndpoint { get; set; }
    public string? TokenDefect { get; set; }
    public string? LogoutDefect { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    public TaskCompletionSource? LogoutGate { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource LogoutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentDictionary<string, string> Challenges { get; } = new();

    private int _tokenPosts;
    private int _logoutPosts;
    public int TokenPosts => _tokenPosts;
    public int LogoutPosts => _logoutPosts;
    public Dictionary<string, string> TokenForm { get; private set; } = new();
    public Dictionary<string, string> LogoutForm { get; private set; } = new();
    public string? LastAuthorizationHeader { get; private set; }
    public string LastAccess { get; private set; } = string.Empty;
    public string LastId { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration")
        {
            if (DiscoveryFails) throw new HttpRequestException(Code);
            return Json(new
            {
                issuer = Issuer,
                authorization_endpoint = CrossOriginAuthorizationEndpoint
                    ? "https://evil.test/authorize"
                    : Issuer + "/authorize",
                token_endpoint = Issuer + "/token",
                jwks_uri = Issuer + "/jwks"
            });
        }
        if (path == "/jwks")
        {
            var parameters = _rsa.ExportParameters(false);
            return Json(new
            {
                keys = new object[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = "RS256",
                        kid = Kid,
                        n = WebEncoders.Base64UrlEncode(parameters.Modulus!),
                        e = WebEncoders.Base64UrlEncode(parameters.Exponent!)
                    }
                }
            });
        }
        if (path == "/token") return await TokenAsync(request, cancellationToken);
        if (path == "/oauth2/logout/requests") return await LogoutAsync(request, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _tokenPosts);
        LastAuthorizationHeader = request.Headers.Authorization?.ToString();
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
            .Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => System.Net.WebUtility.UrlDecode(pair[0]),
                pair => System.Net.WebUtility.UrlDecode(pair[1]));
        // Shared only for assertions: each request works from its own local copy so
        // concurrent redemptions never read another request's verifier.
        TokenForm = form;
        Entered.TrySetResult();
        if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
        var defect = TokenDefect;
        // The real authority redeems only the code it issued; anything else is the
        // fixed invalid_grant of a malformed or replayed code.
        if (form.GetValueOrDefault("code") != Code)
            return new(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { error = "invalid_grant", error_description = Code })
            };
        if (defect == "network") throw new HttpRequestException(Code);
        if (defect == "timeout") throw new TaskCanceledException(Code);
        if (defect is "307" or "308") return new((HttpStatusCode)int.Parse(defect))
        {
            Content = new StringContent("{}"),
            Headers = { Location = new Uri("https://evil.test/" + Code) }
        };
        if (defect == "oversize-body") return new(HttpStatusCode.OK) { Content = new StringContent(new string('a', 65537)) };
        if (defect == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{" + Code) };
        if (defect == "invalid-grant") return new(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error = "invalid_grant", error_description = Code })
        };
        // Select nonce from the PKCE challenge: transaction-specific even under concurrent calls.
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
        var nonce = Challenges[challenge];
        var access = AccessClaims(defect is "nonadmin" or "id-role" ? "student" : "admin");
        var id = AccessClaims(null);
        id.Remove("role");
        id["exp"] = NowSeconds() + 300;
        id["name"] = "id-user";
        id["nonce"] = defect == "nonce" ? "wrong" : nonce;
        if (defect == "id-role") id["role"] = "admin";
        if (defect == "sub") id["sub"] = "other";
        var parts = defect?.Split(':');
        var accessToken = Token("at+jwt", access, parts is { Length: 2 } && parts[0] == "access" ? parts[1] : null);
        var idToken = Token("JWT", id, parts is { Length: 2 } && parts[0] == "id" ? parts[1] : null);
        LastAccess = accessToken;
        LastId = idToken;
        var body = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["id_token"] = idToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 900,
            ["scope"] = "openid profile"
        };
        if (defect == "token-type") body["token_type"] = "Basic";
        if (defect == "expires") body["expires_in"] = 0;
        if (defect == "scope") body["scope"] = "openid offline_access";
        if (defect == "oversize-token") body["access_token"] = new string('a', 8193);
        if (defect == "bad-token") body["id_token"] = "a.b.c";
        if (defect == "missing-id") body.Remove("id_token");
        if (defect == "missing-access") body.Remove("access_token");
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    private async Task<HttpResponseMessage> LogoutAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _logoutPosts);
        LastAuthorizationHeader = request.Headers.Authorization?.ToString();
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
            .Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => System.Net.WebUtility.UrlDecode(pair[0]),
                pair => System.Net.WebUtility.UrlDecode(pair[1]));
        // Shared only for assertions: each request works from its own local copy.
        LogoutForm = form;
        LogoutEntered.TrySetResult();
        if (LogoutGate is not null) await LogoutGate.Task.WaitAsync(cancellationToken);
        var defect = LogoutDefect;
        // The real authority refuses an unusable hint or blank credentials: an empty
        // or oversized id_token_hint, or a Basic header without both client parts,
        // never prepares an upstream logout.
        if (string.IsNullOrEmpty(form.GetValueOrDefault("id_token_hint"))
            || form["id_token_hint"].Length > 8192)
            return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_request" }) };
        try
        {
            var basic = Convert.FromBase64String(
                (request.Headers.Authorization?.Parameter ?? string.Empty).Trim());
            var parts = Encoding.UTF8.GetString(basic).Split(':', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return new(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { error = "invalid_client" }) };
        }
        catch (FormatException)
        {
            return new(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { error = "invalid_client" }) };
        }
        if (defect == "network") throw new HttpRequestException("synthetic-upstream-marker");
        if (defect == "timeout") throw new TaskCanceledException("sensitive-upstream-marker");
        if (defect is "400" or "500" or "503" or "302") return new((HttpStatusCode)int.Parse(defect))
        {
            Content = new StringContent("{}")
        };
        if (defect == "oversize") return new(HttpStatusCode.OK) { Content = new StringContent(new string('a', 4097)) };
        if (defect == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{" + Code) };
        if (defect == "missing-uri") return Json(new { outcome = "ok" });
        if (defect == "non-string") return Json(new { logout_uri = 42 });
        if (defect == "duplicate-field")
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"logout_uri\":\"/oauth2/logout\",\"logout_uri\":\"https://evil.test/l\"}") };
        string value = defect switch
        {
            "evil-host" => "https://evil.test/oauth2/logout?logout_handle=" + Handle,
            "wrong-port" => Issuer + ":843/oauth2/logout?logout_handle=" + Handle,
            "wrong-path" => Issuer + "/other?logout_handle=" + Handle,
            "no-query" => Issuer + "/oauth2/logout",
            "bad-handle" => Issuer + "/oauth2/logout?logout_handle=sensitive-handle-marker",
            "short-handle" => Issuer + "/oauth2/logout?logout_handle=abc",
            "extra-query" => Issuer + "/oauth2/logout?logout_handle=" + Handle + "&x=1",
            "duplicate-handle" => Issuer + "/oauth2/logout?logout_handle=" + Handle + "&logout_handle=" + Handle,
            "fragment" => Issuer + "/oauth2/logout?logout_handle=" + Handle + "#f",
            "userinfo" => "https://user@" + Issuer["https://".Length..] + "/oauth2/logout?logout_handle=" + Handle,
            "relative-evil-host" => "//evil.test/oauth2/logout?logout_handle=" + Handle,
            "relative-wrong-path" => "/other?logout_handle=" + Handle,
            "relative-extra-query" => "/oauth2/logout?logout_handle=" + Handle + "&x=1",
            "relative-bad-handle" => "/oauth2/logout?logout_handle=short",
            "relative-fragment" => "/oauth2/logout?logout_handle=" + Handle + "#f",
            "http-scheme" => "http://" + Issuer["https://".Length..] + "/oauth2/logout?logout_handle=" + Handle,
            "relative" => "/oauth2/logout?logout_handle=" + Handle,
            _ => Issuer + "/oauth2/logout?logout_handle=" + Handle
        };
        return Json(new { logout_uri = value });
    }

    public Dictionary<string, object> AccessClaims(string? role = null) => new()
    {
        ["iss"] = Issuer,
        ["aud"] = "client-id",
        ["sub"] = "account-42",
        ["iat"] = NowSeconds() - 60,
        ["nbf"] = NowSeconds() - 60,
        ["exp"] = NowSeconds() + 900,
        ["name"] = "access-user",
        ["role"] = role ?? "admin"
    };

    public long NowSeconds() => clock.GetUtcNow().ToUnixTimeSeconds();

    public string Token(string type, Dictionary<string, object> claims, string? defect = null)
    {
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["kid"] = Kid, ["typ"] = type };
        if (defect == "typ") header["typ"] = "wrong";
        if (defect == "kid") header["kid"] = "absent";
        if (defect == "alg") header["alg"] = "HS256";
        if (defect == "extra-aud") claims["aud"] = new[] { "client-id", "other-client" };
        if (defect == "empty-sub") claims["sub"] = "";
        if (defect?.StartsWith("missing-", StringComparison.Ordinal) == true) claims.Remove(defect[8..]);
        if (defect is "aud" or "iss") claims[defect] = "wrong";
        if (defect == "exp") claims["exp"] = NowSeconds();
        if (defect == "iat") claims["iat"] = NowSeconds() + 1;
        if (defect == "nbf") claims["nbf"] = NowSeconds() + 60;
        var json = JsonSerializer.Serialize(claims);
        if (defect?.StartsWith("duplicate-", StringComparison.Ordinal) == true)
        {
            var field = defect[10..];
            json = json[..^1] + ",\"" + field + "\":" + JsonSerializer.Serialize(claims[field]) + "}";
        }
        var signing = Base64UrlEncoder.Encode(JsonSerializer.Serialize(header)) + "." + Base64UrlEncoder.Encode(json);
        var signature = _rsa.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (defect == "signature") signature[0] ^= 1;
        return signing + "." + Base64UrlEncoder.Encode(signature);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(body)
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rsa.Dispose();
        base.Dispose(disposing);
    }
}
