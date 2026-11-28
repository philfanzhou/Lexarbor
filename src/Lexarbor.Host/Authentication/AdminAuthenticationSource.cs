namespace Lexarbor.Host.Authentication;

public static class AdminAuthenticationSource
{
    public const string PolicyScheme = "AdminAuthentication";
    public const string PresentationScheme = "AdminAuthentication.Presentation";

    /// <summary>
    /// The cookie scheme holding the PostgreSQL-backed server-side session: its opaque,
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
    // Business paths authenticate only API keys, with no admin session read
    // or administrator token validation.
    public static string Select(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/integration")
            ? BusinessApiKeyAuthentication.SchemeName
            : IsBearer(request)
            ? JwtBearerDefaultsName
            : SessionScheme;

    private const string JwtBearerDefaultsName =
        Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;

    public static bool IsBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}
