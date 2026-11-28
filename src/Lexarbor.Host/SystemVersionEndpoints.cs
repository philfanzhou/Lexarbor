using Lexarbor.Service;
using ServiceMantle.Web.Management;

namespace Lexarbor.Host;

internal static class SystemVersionEndpoints
{
    internal const string Path = "/admin/system/version";

    public static void MapSystemVersion(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, () => ApiResponse.Ok(new
        {
            version = ApplicationVersion.Current,
            revision = ApplicationVersion.Revision,
            channel = ApplicationVersion.Channel
        })).RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName);
    }
}
