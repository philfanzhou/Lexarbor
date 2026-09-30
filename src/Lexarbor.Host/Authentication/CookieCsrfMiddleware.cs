using Lexarbor.Service;

namespace Lexarbor.Host.Authentication;

public sealed class CookieCsrfMiddleware
{
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            HttpMethods.Get,
            HttpMethods.Head,
            HttpMethods.Options,
            HttpMethods.Trace
        };

    private readonly RequestDelegate _next;

    public CookieCsrfMiddleware(
        RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/admin") ||
            SafeMethods.Contains(context.Request.Method) ||
            !AdminAuthenticationSource.IsCookie(context.Request) ||
            context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        if (!string.Equals(
                context.Request.Headers["X-Requested-With"],
                "XMLHttpRequest",
                StringComparison.Ordinal))
        {
            await VocabularyHttpResponse.WriteFailureAsync(
                context.Response,
                StatusCodes.Status403Forbidden,
                "The requested admin operation failed CSRF validation.");
            return;
        }

        await _next(context);
    }
}
