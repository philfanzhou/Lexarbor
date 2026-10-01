using Lexarbor.Service;

namespace Lexarbor.Host;

internal static class SystemVersionEndpoints
{
    internal const string Path = "/admin/system/version";

    public static void MapSystemVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, () => VocabularyHttpResponse.Ok(new
        {
            version = ApplicationVersion.Current,
            revision = ApplicationVersion.Revision,
            channel = ApplicationVersion.Channel
        })).RequireAuthorization("VocabularyAdmin");
    }
}
