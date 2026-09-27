using Lexarbor.Service;

namespace Lexarbor.Host;

internal static class SystemVersionEndpoints
{
    internal const string Path = "/admin/system/version";

    public static void UseSystemVersionNoStore(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            // Match routing's case/trailing-slash behavior, including rejected requests.
            if (context.Request.Path.Value?.TrimEnd('/').Equals(Path, StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    return Task.CompletedTask;
                });
            }
            await next(context);
        });
    }

    public static void MapSystemVersionEndpoints(this WebApplication app)
    {
        app.MapGet(Path, () => VocabularyHttpResponse.Ok(new
        {
            version = ApplicationVersion.Current,
            revision = ApplicationVersion.Revision,
            channel = ApplicationVersion.Channel
        })).RequireAuthorization("VocabularyAdmin");
    }
}
