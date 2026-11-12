using Lexarbor.Service;
using Microsoft.AspNetCore.Antiforgery;
using ServiceMantle.Audit;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The session-identity bridge behind <c>UseAuthentication</c>: a principal the hosted-login
/// cookie session scheme authenticated gets the ServiceMantle management identity minted from
/// its own verified claims — never accepted from the outside — exactly as the retired in-house
/// handler did. The same middleware carries the session CSRF boundary: a session-authenticated
/// unsafe method on the administration surface must present the antiforgery pair issued by
/// <c>GET /admin/auth/csrf</c>, and a missing or wrong pair fails the session authentication
/// itself with the fixed 401 — the boundary's historical shape under the session scheme. The
/// same middleware keeps logout the idempotent browser cleanup it always was: the hosted
/// session cookie and the retired password-login JWT cookie are deleted on every logout
/// response.
/// </summary>
public sealed class AdminSessionIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var identity = context.User.Identity;
        if (identity?.IsAuthenticated == true
            && string.Equals(
                identity.AuthenticationType,
                AdminAuthenticationSource.SessionScheme,
                StringComparison.Ordinal))
        {
            // The CSRF boundary covers the whole administration surface, the hosted endpoints
            // under /admin/auth included: the package's session scheme enforced the boundary on
            // every route that authenticated against it, so an unknown /admin/auth write answers
            // the fixed 401 exactly like a protected one. The one exception is the logout
            // route, which owns its own antiforgery presentation (the fixed 400
            // csrf_rejected outcome).
            if (!HttpMethods.IsGet(context.Request.Method)
                && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method)
                && !HttpMethods.IsTrace(context.Request.Method)
                && context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
                && !context.Request.Path.StartsWithSegments("/admin/auth/logout", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>()
                        .ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    // The session authentication itself fails closed: the fixed 401, and the
                    // endpoint never runs.
                    await VocabularyHttpResponse.WriteFailureAsync(
                        context.Response, StatusCodes.Status401Unauthorized, "Authentication is required.");
                    return;
                }
            }

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

        // Logout is idempotent browser cleanup: the session cookie is deleted on every
        // logout response, so a browser holding an unusable handle is cleaned up the same as
        // a live one. The deletion goes through AdminSessionCookie.Clear so it names and
        // attributes the session cookie of the active profile (the derived name under a
        // plain-HTTP redirect URI). The cookie handler's own session-cookie deletion (written
        // only for a live revocation) carries the same name and path, so it replaces this
        // entry instead of duplicating it; the storage-failure middleware's cleanup does too.
        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/admin/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            AdminSessionCookie.Clear(context);
        }

        await next(context);
    }
}
