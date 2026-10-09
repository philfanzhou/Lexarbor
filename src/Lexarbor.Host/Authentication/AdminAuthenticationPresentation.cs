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

    // Bearer credentials come only from the Authorization header; every other
    // request — cookie-bearing or anonymous — is answered by the hosted-login
    // session scheme of the official SignaCore client. Authentication,
    // challenge and forbid must never choose a fallback identity, so a legacy
    // JWT cookie can no longer authenticate anything.
    public static string Select(HttpRequest request) =>
        IsBearer(request)
            ? JwtBearerDefaultsName
            : SignaCore.Client.AspNetCore.SignaCoreHostedLoginDefaults.SessionAuthenticationScheme;

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
    /// handles and protected payloads keep authenticating across the migration (and
    /// across a rollback). It is applied to the SignaCore client options at
    /// registration when the redirect URI is not plain HTTP.
    /// </summary>
    public const string Name = "__Host-Lexarbor.AdminSession";

    /// <summary>
    /// The session cookie of the plain-HTTP profile: the same name without the
    /// <c>__Host-</c> prefix. The official client (0.1.16) derives its whole cookie
    /// profile from the redirect URI's scheme and refuses a prefixed session-cookie
    /// name against an <c>http</c> redirect URI — the prefix demands the Secure
    /// attribute, which a plain-HTTP deployment cannot set.
    /// </summary>
    public const string PlainHttpName = "Lexarbor.AdminSession";

    /// <summary>
    /// The session cookie name for a configured redirect URI, by the same rule the
    /// official client applies to its cookie profile: an absolute plain-<c>http</c>
    /// redirect URI rides the plain-HTTP profile and drops the <c>__Host-</c> prefix;
    /// everything else — <c>https</c>, or a blank redirect URI in the optional-login
    /// mode — keeps the historical byte-for-byte name.
    /// </summary>
    public static string ForRedirectUri(string? redirectUri) =>
        IsPlainHttpRedirect(redirectUri) ? PlainHttpName : Name;

    /// <summary>
    /// Whether the plain-HTTP cookie profile is active for this redirect URI — the
    /// same input the official client's profile reads (an absolute <c>http</c> redirect
    /// URI; illegal values fail its startup validation and never reach a request).
    /// </summary>
    internal static bool IsPlainHttpRedirect(string? redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>
    /// The retired password-login JWT cookie. It stopped being authenticated when the
    /// password proxy was removed; logout and sign-in keep deleting it so a browser
    /// that still carries one is cleaned up at the first boundary event.
    /// </summary>
    public const string LegacyJwtName = "lexarborAdmin";

    internal static CookieOptions LegacyOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true
    };

    /// <summary>
    /// Deletes the session cookie the active profile actually issues — read from the
    /// SignaCore client options, the single place the derived name exists — with the
    /// attributes of that profile: a plain-HTTP deletion must not carry Secure, or a
    /// real browser would refuse the header and keep the cookie. The retired
    /// password-login cookie is deleted beside it.
    /// </summary>
    public static void Clear(HttpContext context)
    {
        var login = context.RequestServices.GetRequiredService<
            Microsoft.Extensions.Options.IOptionsMonitor<SignaCore.Client.AspNetCore.SignaCoreHostedLoginOptions>>()
            .CurrentValue;
        context.Response.Cookies.Delete(login.SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = !IsPlainHttpRedirect(login.RedirectUri),
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
        context.Response.Cookies.Delete(LegacyJwtName, LegacyOptions());
    }
}

/// <summary>
/// The thin presentation handler behind the administrator policy scheme's challenge
/// and forbid answers: the management API's fixed JSON 401/403 envelope. It never
/// authenticates; the SignaCore session handler's 302-to-start challenge stays out
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
