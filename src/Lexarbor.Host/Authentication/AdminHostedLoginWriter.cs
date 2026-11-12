using System.Security.Claims;
using Lexarbor.Service;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The fixed presentation layer of the admin hosted-login slice (the framework's OpenID Connect
/// handler defines the protocol; this writer keeps the browser-facing contract of the replaced
/// SignaCore client integration): the administrator API's fixed JSON envelopes, the in-site
/// redirects and the reason vocabulary. The value of <c>reason</c> is both the redirect
/// parameter and the failed-login audit row's fixed classification.
/// </summary>
public static class AdminHostedLoginWriter
{
    public const string SignInFailedTarget = "/#/login?reason=sign_in_failed";
    public const string DeniedTarget = "/#/login?reason=denied";
    public const string CancelledTarget = "/#/login?reason=canceled";
    public const string ProviderUnavailableTarget = "/#/login?reason=provider_unavailable";
    public const string LoggedOutTarget = "/#/login?reason=logged_out";
    public const string LogoutFailedTarget = "/#/login?reason=logout_failed";

    /// <summary>The redirect target a completed sign-out hands the browser (the protected state
    /// of the standard RP-initiated logout ends here).</summary>
    public const string PostLogoutReturnTarget = LoggedOutTarget;

    /// <summary>
    /// The callback reason vocabulary: the pre-sign-in gate's denial and the user's cancellation
    /// are the two refusals; a transport failure of the authority's Discovery, JWKS or token
    /// endpoints — backchannel timeouts included, which the timeout handler normalizes into
    /// transport failures — is a provider outage, as is an upstream authorization error naming
    /// one (<c>server_error</c>, <c>temporarily_unavailable</c>); everything else is the generic
    /// sign-in failure. No other failure detail ever reaches the browser.
    /// </summary>
    public static string CallbackReason(Exception? failure)
    {
        if (failure is null)
        {
            return "sign_in_failed";
        }

        if (failure.Message == AdminAuthenticationSource.NotAdminFailure)
        {
            return "denied";
        }

        for (var exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectProtocolException protocol
                && protocol.Data["error"] is string error)
            {
                return error switch
                {
                    "access_denied" => "canceled",
                    "server_error" or "temporarily_unavailable" => "provider_unavailable",
                    _ => "sign_in_failed"
                };
            }
        }

        return IsAuthorityUnavailable(failure) ? "provider_unavailable" : "sign_in_failed";
    }

    /// <summary>
    /// Whether the failure chain is a transport failure of the authority's endpoints — the
    /// provider-unavailable classification. IdentityModel wraps Discovery and document retrieval
    /// errors, so every link of the chain is inspected; client-side timeouts arrive here as
    /// <see cref="HttpRequestException"/> through the backchannel timeout handler.
    /// </summary>
    internal static bool IsAuthorityUnavailable(Exception failure)
    {
        for (var exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is HttpRequestException or TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The start route's three fixed JSON failures: an invalid <c>returnUrl</c> answers the
    /// fixed 400 body, an unreachable authority the fixed 502, and the unconfigured degradation
    /// the fixed 503. No pending sign-in is ever created for a rejected input.
    /// </summary>
    public static Task WriteStartFailureAsync(HttpContext context, AdminStartFailure failure,
        CancellationToken cancellationToken) => failure switch
    {
        AdminStartFailure.InvalidReturnUrl => VocabularyHttpResponse
            .BadRequest("The login return target is invalid.").ExecuteAsync(context),
        AdminStartFailure.AuthorityUnreachable => VocabularyHttpResponse
            .BadGateway("The authentication provider is unavailable.").ExecuteAsync(context),
        _ => VocabularyHttpResponse
            .ServiceUnavailable("Hosted authentication is not configured.").ExecuteAsync(context),
    };

    /// <summary>The session-status endpoint's fixed shape: the authorized administrator
    /// identity's display name and roles — never a token, never another reason.</summary>
    public static Task WriteSessionStatusAsync(HttpContext context, ClaimsPrincipal? presented,
        CancellationToken cancellationToken)
    {
        // The presented principal is whichever authorized administrator credential reached the
        // endpoint — the hosted session, or a Bearer token through the scheme selector; an
        // anonymous or expired request never reaches this writer (the endpoint's policy answers
        // the fixed 401 first).
        var username = VocabularyClaims.GetDisplayName(presented) ?? string.Empty;
        var roles = VocabularyClaims.GetRoles(presented);
        return VocabularyHttpResponse.Ok(new { username, roles }).ExecuteAsync(context);
    }

    /// <summary>The antiforgery-rejected logout: the package's fixed bare
    /// <c>{"outcome":"csrf_rejected"}</c> body; no session is touched.</summary>
    public static async Task WriteLogoutCsrfRejectedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { outcome = "csrf_rejected" }, context.RequestAborted);
    }

    /// <summary>The local-only logout: the bare success envelope with no upstream echo.</summary>
    public static Task WriteLogoutLocalOnlyAsync(HttpContext context) =>
        VocabularyHttpResponse.Ok().ExecuteAsync(context);

    /// <summary>The prepared logout: the success envelope carrying the verified end-session
    /// URL, which the SPA follows with a top-level navigation.</summary>
    public static Task WriteLogoutPreparedAsync(HttpContext context, string logoutUrl) =>
        VocabularyHttpResponse.Ok(new { logoutUrl }).ExecuteAsync(context);

    /// <summary>
    /// The security headers the routed /admin surface gets from its endpoint markers. The
    /// protocol handler answers the callback and the sign-out callback before routing — these
    /// two responses carry the same baseline here.
    /// </summary>
    public static void ApplyCallbackSafetyHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }
}

public enum AdminStartFailure
{
    InvalidReturnUrl,
    AuthorityUnreachable,
    NotConfigured,
}

/// <summary>
/// The allowlist of local routes a completed hosted sign-in may land on. The answer is
/// normalized to the SPA's <c>/#/…</c> form; anything outside the allowlist is rejected as the
/// fixed invalid-return-url failure. A start without a <c>returnUrl</c> never consults this
/// validator: it lands on the fixed application-root default.
/// </summary>
public static class AdminLoginReturnTarget
{
    private static readonly HashSet<string> Routes = new(StringComparer.Ordinal)
    {
        "/settings", "/settings/audio-storage", "/settings/review", "/settings/text", "/settings/speech", "/settings/background",
        "/enrichment", "/books", "/vocabulary", "/phrases", "/import", "/import/phrase", "/import/batch"
    };

    public static string? Normalize(string? route)
    {
        if (route is null) return null;
        if (route.Length is 0 or > 256) return null;
        if (route.StartsWith("/#", StringComparison.Ordinal)) route = route[2..];
        if (route.Any(c => c is '%' or '?' or '#' or '\\' || char.IsControl(c))) return null;
        if (Routes.Contains(route)) return "/#" + route;
        var segments = route.Split('/');
        if (segments.Length == 4 && segments[0] == "" && segments[1] == "books" && segments[3] == "words"
            && segments[2].Length is >= 1 and <= 128 && segments[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return "/#" + route;
        return null;
    }
}
