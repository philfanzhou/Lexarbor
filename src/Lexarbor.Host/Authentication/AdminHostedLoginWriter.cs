using System.Security.Claims;
using Lexarbor.Service;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The Lexarbor presentation of the official hosted-login package's protocol outcomes: the
/// administrator API's fixed JSON envelopes, in-site redirects and reason vocabulary. The
/// package owns every protocol decision; this writer only shapes what the browser sees, so
/// the external contracts keep their pre-migration form (the declared changes in
/// <c>docs/development/HostedLoginReleaseNotes.md</c> excepted). The failed-login audit
/// writer is resolved from the failing request's own scope, so no scoped dependency is
/// captured by this singleton.
/// </summary>
public sealed class AdminHostedLoginResponseWriter : ISignaCoreHostedLoginResponseWriter
{
    private const string LoggedOutTarget = "/#/login?reason=logged_out";
    private const string LogoutFailedTarget = "/#/login?reason=logout_failed";

    public Task WriteSignInUnavailableAsync(HttpContext context, CancellationToken cancellationToken) =>
        VocabularyHttpResponse.ServiceUnavailable("Hosted authentication is not configured.").ExecuteAsync(context);

    /// <summary>
    /// Maps the package's closed failure reasons onto the repository's word list. The start
    /// and callback routes share this writer: a start failure keeps its fixed JSON answer,
    /// a callback failure first records the fixed-reason failed-login audit row (skipped
    /// only for an already-cancelled request, which is not a completed security event) and
    /// then answers with the unchanged in-site redirect.
    /// </summary>
    public async Task WriteSignInFailureAsync(HttpContext context, SignaCoreSignInReason reason,
        CancellationToken cancellationToken)
    {
        if (context.Request.Path.StartsWithSegments("/admin/auth/callback", StringComparison.OrdinalIgnoreCase))
        {
            var vocabulary = CallbackReason(context, reason);
            if (!context.RequestAborted.IsCancellationRequested)
            {
                await context.RequestServices.GetRequiredService<AdminAuthenticationAudit>()
                    .RecordLoginFailedAsync(context, vocabulary, context.RequestAborted);
            }
            Redirect(context, "/#/login?reason=" + vocabulary);
            return;
        }

        switch (reason)
        {
            case SignaCoreSignInReason.InvalidReturnUrl:
                await VocabularyHttpResponse.BadRequest("The login return target is invalid.").ExecuteAsync(context);
                break;
            case SignaCoreSignInReason.AuthorityUnreachable:
                await VocabularyHttpResponse.BadGateway("The authentication provider is unavailable.").ExecuteAsync(context);
                break;
            default:
                // The pending-sign-in store is full: the service is up, but no new handshake
                // can start right now. Any other reason on the start route cannot occur.
                await VocabularyHttpResponse.ServiceUnavailable("Hosted authentication is temporarily unavailable.")
                    .ExecuteAsync(context);
                break;
        }
    }

    public Task WriteFailurePageAsync(HttpContext context, SignaCoreSignInReason? reason,
        CancellationToken cancellationToken)
    {
        // Lexarbor presents sign-in failures through the SPA's login route, never through a
        // server-rendered failure page; the mapped page exists only for a direct visit.
        Redirect(context, "/#/login");
        return Task.CompletedTask;
    }

    public Task WriteSessionStatusAsync(HttpContext context, SignaCoreSessionStatus status,
        CancellationToken cancellationToken) =>
        WriteSessionStatusAsync(context, status, null, cancellationToken);

    public Task WriteSessionStatusAsync(HttpContext context, SignaCoreSessionStatus status,
        ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        // The ticket's principal is the hosted session's own; a caller that reached the
        // endpoint through another authorized administrator credential (a Bearer token)
        // sees that verified identity instead, keeping the endpoint's historical answer
        // for every administrator. An anonymous or expired request gets the fixed 401.
        var presented = principal is { Identity.IsAuthenticated: true }
            ? principal
            : context.User is { Identity.IsAuthenticated: true } ? context.User : null;
        if (presented is null)
        {
            return VocabularyHttpResponse.WriteFailureAsync(
                context.Response, StatusCodes.Status401Unauthorized, "Authentication is required.");
        }
        var username = VocabularyClaims.GetDisplayName(presented) ?? string.Empty;
        var roles = VocabularyClaims.GetRoles(presented);
        return VocabularyHttpResponse.Ok(new { username, roles }).ExecuteAsync(context);
    }

    public Task WriteLogoutPreparedAsync(HttpContext context, string logoutUri,
        CancellationToken cancellationToken) =>
        VocabularyHttpResponse.Ok(new { logoutUrl = logoutUri }).ExecuteAsync(context);

    public Task WriteLogoutLocalOnlyAsync(HttpContext context, CancellationToken cancellationToken) =>
        VocabularyHttpResponse.Ok().ExecuteAsync(context);

    public Task WriteLogoutReturnFailedAsync(HttpContext context, CancellationToken cancellationToken)
    {
        Redirect(context, LogoutFailedTarget);
        return Task.CompletedTask;
    }

    /// <summary>The redirect target the package's logout-return endpoint hands the browser.</summary>
    public const string PostLogoutReturnTarget = LoggedOutTarget;

    /// <summary>
    /// The callback reason vocabulary: <see cref="SignaCoreSignInReason.UserCanceled"/> and
    /// <see cref="SignaCoreSignInReason.PreSignInDenied"/> are the two refusals; an
    /// unreachable authority on the callback leg is a provider outage; everything else is
    /// the generic sign-in failure — except that a malformed authorization response whose
    /// upstream <c>error</c> code names a provider outage (<c>server_error</c>,
    /// <c>temporarily_unavailable</c>) keeps the historical <c>provider_unavailable</c>
    /// classification, so the browser and the audit row keep the full four-value
    /// vocabulary. The value is both the redirect parameter and the failed-login audit
    /// row's fixed classification.
    /// </summary>
    private static string CallbackReason(HttpContext context, SignaCoreSignInReason reason) => reason switch
    {
        SignaCoreSignInReason.UserCanceled => "canceled",
        SignaCoreSignInReason.PreSignInDenied => "denied",
        SignaCoreSignInReason.AuthorityUnreachable => "provider_unavailable",
        SignaCoreSignInReason.InvalidResponse
            when context.Request.Query.TryGetValue("error", out var error)
            && error.Count == 1
            && error[0] is "server_error" or "temporarily_unavailable" => "provider_unavailable",
        _ => "sign_in_failed"
    };

    private static void Redirect(HttpContext context, string target)
    {
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = target;
    }
}

/// <summary>
/// The allowlist of local routes a completed hosted sign-in may land on, expressed as the
/// official package's return-URL validator. The answer is normalized to the SPA's
/// <c>/#/…</c> form and re-checked by the package's own local-path rule; anything outside
/// the allowlist is rejected as the fixed <c>invalid_return_url</c> reason. A start without
/// a <c>returnUrl</c> never consults this validator: the package lands it on its fixed
/// application-root default.
/// </summary>
public static class AdminLoginReturnTarget
{
    private static readonly HashSet<string> Routes = new(StringComparer.Ordinal)
    {
        "/books", "/vocabulary", "/phrases", "/import", "/import/phrase", "/import/batch"
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
