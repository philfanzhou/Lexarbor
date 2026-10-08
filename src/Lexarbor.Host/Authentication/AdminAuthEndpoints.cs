using Lexarbor.Host.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ServiceMantle.Web.Management;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The hosted administrator login surface, mapped by the official
/// <c>SignaCore.Client.AspNetCore</c> package under the repository's historical
/// <c>/admin/auth</c> prefix (start, callback, session, csrf, logout, logout/return and the
/// package's fixed failure page). The package owns the protocol; this mapper adds the
/// repository's three selective conventions on top of the returned endpoint group: the
/// start endpoint alone carries the configured admin-login rate limit, the logout endpoint
/// alone carries the session-storage failure metadata, and the session endpoint alone gains
/// the ServiceMantle administrator policy beside the package's authentication requirement.
/// </summary>
public static class AdminAuthEndpoints
{
    public const string Prefix = "/admin/auth";

    public static IEndpointConventionBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapSignaCoreHostedLogin(Prefix);
        var rateLimitEnabled = app.ServiceProvider
            .GetRequiredService<IOptions<RateLimitOptions>>().Value.AdminLogin.Enabled;
        group.Add(builder =>
        {
            if (builder is not RouteEndpointBuilder route) return;
            var path = route.RoutePattern.RawText ?? string.Empty;
            if (IsSegment(path, Prefix + "/" + SignaCoreHostedLoginDefaults.StartPathSegment))
            {
                if (rateLimitEnabled)
                {
                    route.Metadata.Add(new EnableRateLimitingAttribute(RateLimitingExtensions.AdminLoginPolicy));
                }
                else
                {
                    route.Metadata.Add(new DisableRateLimitingAttribute());
                }
            }
            else if (IsSegment(path, Prefix + "/" + SignaCoreHostedLoginDefaults.LogoutPathSegment))
            {
                route.Metadata.Add(new AdminSessionLogoutMetadata());
            }
            else if (IsSegment(path, Prefix + "/" + SignaCoreHostedLoginDefaults.SessionPathSegment))
            {
                route.Metadata.Add(new AuthorizeAttribute { Policy = ManagementAuthorizationDefaults.AdminPolicyName });
            }
        });
        return group;
    }

    /// <summary>
    /// Whether a route template is exactly the given path, ignoring case: the package maps
    /// fixed literal segments, so an ordinal-ignore-case equality is the whole match.
    /// </summary>
    private static bool IsSegment(string template, string path) =>
        string.Equals(template.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase);
}
