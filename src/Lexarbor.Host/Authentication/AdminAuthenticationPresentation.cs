using System.Text.Encodings.Web;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

public static class AdminAuthenticationSource
{
    public const string PolicyScheme = "AdminAuthentication";
    public const string PresentationScheme = "AdminAuthentication.Presentation";
    public const string IntegrationScheme = "IntegrationService";

    /// <summary>
    /// The cookie scheme holding the SQLite-backed server-side session: its opaque,
    /// data-protected cookie references a row in the administrator session store. Named after
    /// the public cookie it owns; replaces the SignaCore package's session scheme.
    /// </summary>
    public const string SessionScheme = "Lexarbor.AdminSession";

    /// <summary>The OpenID Connect scheme driving the authorization-code flow and the standard
    /// RP-initiated logout against the configured authority.</summary>
    public const string OidcScheme = "Lexarbor.AdminOidc";

    /// <summary>The named <c>HttpClient</c> wired as the OpenID Connect backchannel: bounded to
    /// 30 seconds, redirect-free, cookie-free, unlogged, and carrying the timeout
    /// normalization plus the ServiceMantle correlation propagation.</summary>
    public const string OidcHttpClientName = "Lexarbor.AdminOidc";

    /// <summary>
    /// The historical request-header name of the hosted-login antiforgery boundary. The SPA
    /// echoes it on every write; the name is kept byte for byte across the handler swap so the
    /// browser contract is unchanged.
    /// </summary>
    public const string CsrfHeaderName = "X-SignaCore-CSRF";

    /// <summary>The antiforgery cookie issued alongside the token by <c>GET /admin/auth/csrf</c>.</summary>
    public const string CsrfCookieName = "Lexarbor.AdminCsrf";

    /// <summary>
    /// The failure marker of the pre-sign-in administrator gate: a subject whose re-verified
    /// access token carries no administrator role fails token validation before any session is
    /// written, and the failure surfaces to the browser as the fixed <c>denied</c> landing.
    /// </summary>
    internal const string NotAdminFailure = "The signed-in subject is not an administrator.";

    /// <summary>
    /// The failure marker of an authorization response that is not a usable, single-authority
    /// response: duplicated protocol parameters, a missing RFC 9207 issuer parameter, or an
    /// issuer that does not match the Discovery document.
    /// </summary>
    internal const string InvalidResponseFailure = "The authorization response is invalid.";

    /// <summary>
    /// The per-request marker the ticket store sets when a logout actually ended a live
    /// session: only that request may hand the browser the upstream end-session URL.
    /// </summary>
    internal const string RevokedLiveSessionItemKey = "Lexarbor.AdminSession.RevokedLive";

    /// <summary>
    /// The ticket-property key carrying the verified ID-token issuer into the session store
    /// (the validated principal itself omits the consumed <c>iss</c> claim).
    /// </summary>
    internal const string OidcIssuerPropertiesKey = "Lexarbor.AdminOidc.Issuer";

    // Bearer credentials come only from the Authorization header; every other
    // request — cookie-bearing or anonymous — is answered by the hosted-login
    // session scheme. Authentication, challenge and forbid must never choose a
    // fallback identity, so a legacy JWT cookie can no longer authenticate anything.
    // The integration surface is authenticated only by integration service
    // tokens: its bearer header is never handed to the identity provider's JWT
    // handler, and no administrator session is read for it.
    public static string Select(HttpRequest request) =>
        request.Path.StartsWithSegments("/integration")
            ? IntegrationScheme
            : IsBearer(request)
            ? JwtBearerDefaultsName
            : SessionScheme;

    private const string JwtBearerDefaultsName =
        Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;

    public static bool IsBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}

public static class AdminSessionCookie
{
    /// <summary>
    /// The opaque hosted-login session cookie of the HTTPS profile. The name is
    /// byte-for-byte the one the retired in-house implementation issued, so existing
    /// rows and protected payloads keep authenticating across the migration (and
    /// across a rollback). It is applied to the cookie session scheme at
    /// registration when the redirect URI is not plain HTTP.
    /// </summary>
    public const string Name = "__Host-Lexarbor.AdminSession";

    /// <summary>
    /// The session cookie of the plain-HTTP profile: the same name without the
    /// <c>__Host-</c> prefix. The cookie profile derives from the redirect URI's
    /// scheme: an <c>http</c> redirect rides the plain-HTTP profile and drops the
    /// <c>__Host-</c> prefix — the prefix demands the Secure attribute, which a
    /// plain-HTTP deployment cannot set.
    /// </summary>
    public const string PlainHttpName = "Lexarbor.AdminSession";

    /// <summary>
    /// The session cookie name for a configured redirect URI, by the same rule the
    /// cookie session scheme registration applies: an absolute plain-<c>http</c>
    /// redirect URI rides the plain-HTTP profile and drops the <c>__Host-</c> prefix;
    /// everything else — <c>https</c>, or a blank redirect URI in the optional-login
    /// mode — keeps the historical byte-for-byte name.
    /// </summary>
    public static string ForRedirectUri(string? redirectUri) =>
        IsPlainHttpRedirect(redirectUri) ? PlainHttpName : Name;

    /// <summary>
    /// Whether the plain-HTTP cookie profile is active for this redirect URI — the
    /// same input the cookie scheme registration reads (an absolute <c>http</c>
    /// redirect URI; illegal values fail startup validation and never reach a request).
    /// </summary>
    internal static bool IsPlainHttpRedirect(string? redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>
    /// Deletes the session cookie the active profile actually issues — derived from the
    /// configured redirect URI by the same rule the cookie scheme registration applies — with
    /// the attributes of that profile: a plain-HTTP deletion must not carry Secure, or a real
    /// browser would refuse the header and keep the cookie.
    /// </summary>
    public static void Clear(HttpContext context)
    {
        var code = context.RequestServices.GetRequiredService<
            Microsoft.Extensions.Options.IOptionsMonitor<OidcCodeOptions>>()
            .CurrentValue;
        context.Response.Cookies.Delete(ForRedirectUri(code.RedirectUri), new CookieOptions
        {
            HttpOnly = true,
            Secure = !IsPlainHttpRedirect(code.RedirectUri),
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }
}

/// <summary>
/// The thin presentation handler behind the administrator policy scheme's challenge
/// and forbid answers: the management API's fixed JSON 401/403 envelope. It never
/// authenticates; the cookie session handler's 302-to-start challenge stays out
/// of the API surface because the policy scheme forwards challenge and forbid here.
/// </summary>
public sealed class AdminAuthenticationPresentationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        VocabularyHttpResponse.WriteFailureAsync(Response, StatusCodes.Status401Unauthorized, "Authentication is required.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        VocabularyHttpResponse.WriteFailureAsync(Response, StatusCodes.Status403Forbidden, "Administrator role is required.");
}

// Covers both authentication Read and endpoint Revoke; neither may bypass browser
// cleanup on logout when storage cannot confirm revocation. Never log provider details.
internal sealed class AdminSessionLogoutMetadata;

public sealed class AdminSessionFailureMiddleware(RequestDelegate next,
    ILogger<AdminSessionFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception exception) when (!context.Response.HasStarted
            && exception is AdminSessionStorageException or StorageBusyException
            && exception.Message == AdminSessionRepository.StorageFailureMessage)
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<AdminSessionLogoutMetadata>() is not null)
                AdminSessionCookie.Clear(context);
            var busy = exception is StorageBusyException;
            if (busy) context.Response.Headers.RetryAfter = "1";
            logger.LogWarning("Administrator session storage operation could not be confirmed.");
            await VocabularyHttpResponse.WriteFailureAsync(context.Response,
                busy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                AdminSessionRepository.StorageFailureMessage);
        }
    }
}
