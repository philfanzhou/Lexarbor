using System.Security.Claims;
using System.Text.Encodings.Web;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

public static class AdminAuthenticationSource
{
    public const string PolicyScheme = "AdminAuthentication";
    public const string SessionScheme = "AdminSession";

    // Bearer credentials come only from the Authorization header; every other
    // request — cookie-bearing or anonymous — is answered by the session scheme.
    // Authentication, challenge, CSRF and logout must never choose a fallback
    // identity, so a legacy JWT cookie can no longer authenticate anything.
    public static string Select(HttpRequest request) =>
        IsCookie(request)
            ? SessionScheme
            : JwtBearerDefaults.AuthenticationScheme;

    public static bool IsCookie(HttpRequest request) =>
        !request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}

public static class AdminSessionCookie
{
    public const string Name = "__Host-Lexarbor.AdminSession";

    /// <summary>
    /// The retired password-login JWT cookie. It stopped being authenticated when the
    /// password proxy was removed; logout keeps deleting it so a browser that still
    /// carries one is cleaned up at the first sign-out.
    /// </summary>
    public const string LegacyJwtName = "lexarborAdmin";

    public static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true
    };

    internal static CookieOptions LegacyOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true
    };

    public static void Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(Name, Options());
        context.Response.Cookies.Delete(LegacyJwtName, LegacyOptions());
    }
}

public sealed class AdminSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    AdminSessionStore store) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Request.Cookies.TryGetValue(AdminSessionCookie.Name, out var handle);
        var session = await store.ReadAsync(handle, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Administrator session is invalid or expired.");
        return AuthenticateResult.Success(new AuthenticationTicket(CreatePrincipal(session), Scheme.Name));
    }

    internal static ClaimsPrincipal CreatePrincipal(ValidatedAdminSession session)
    {
        List<Claim> claims = [new("iss", session.Issuer), new("sub", session.Subject)];
        if (!string.IsNullOrWhiteSpace(session.DisplayName)) claims.Add(new("name", session.DisplayName));
        claims.AddRange(session.Roles.Select(role => new Claim("role", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AdminAuthenticationSource.SessionScheme, "name", "role"));
    }

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
