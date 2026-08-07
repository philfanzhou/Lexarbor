using Microsoft.Extensions.Options;
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
    private readonly AdminAuthenticationOptions _options;

    public CookieCsrfMiddleware(
        RequestDelegate next,
        IOptions<AdminAuthenticationOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/admin") ||
            SafeMethods.Contains(context.Request.Method) ||
            HasBearerAuthorization(context.Request) ||
            context.User.Identity?.IsAuthenticated != true ||
            !context.Request.Cookies.ContainsKey(_options.CookieName))
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

    private static bool HasBearerAuthorization(HttpRequest request)
    {
        return request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }
}
