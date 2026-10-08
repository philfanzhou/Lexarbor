using Lexarbor.Service;
using ServiceMantle.Audit;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The session-identity bridge behind <c>UseAuthentication</c>: a principal the official
/// hosted-login session scheme authenticated gets the ServiceMantle management identity
/// minted from its own verified claims — never accepted from the outside — exactly as the
/// retired in-house handler did. The same middleware keeps logout the idempotent browser
/// cleanup it always was: both the hosted session cookie and the retired password-login
/// JWT cookie are deleted on every logout response (the sign-in callback cleans the
/// legacy cookie only after its replacement commits, inside the ticket store).
/// </summary>
public sealed class AdminSessionIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var identity = context.User.Identity;
        if (identity?.IsAuthenticated == true
            && string.Equals(
                identity.AuthenticationType,
                SignaCoreHostedLoginDefaults.SessionAuthenticationScheme,
                StringComparison.Ordinal))
        {
            // The interactive session is a ServiceMantle interactive administrator; any
            // servicemantle.* claim the stored principal carried was already removed when
            // the mapping ran at sign-in time, and this re-run keeps the projection stable.
            ManagementIdentityMapping.Apply(
                context.User,
                context.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AdminAuthenticationOptions>>()
                    .CurrentValue.RequiredRole,
                WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value);
        }

        // Logout is idempotent browser cleanup: both cookies are deleted on every logout
        // response, so a browser holding an unusable handle is cleaned up the same as a
        // live one. The official handler's own session-cookie deletion (written only for
        // a live revocation) carries the same name and path, so it replaces this entry
        // instead of duplicating it; the storage-failure middleware's cleanup does too.
        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/admin/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Cookies.Delete(AdminSessionCookie.Name, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });
            context.Response.Cookies.Delete(AdminSessionCookie.LegacyJwtName, AdminSessionCookie.LegacyOptions());
        }

        await next(context);
    }
}
