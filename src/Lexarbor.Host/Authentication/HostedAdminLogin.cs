using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

public static class HostedAdminLogin
{
    public static void MapHostedAdminLogin(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/auth/start", StartAsync).AllowAnonymous().RequireRateLimiting(RateLimitingExtensions.AdminLoginPolicy);
        app.MapGet("/admin/auth/callback", CallbackAsync).AllowAnonymous();
    }

    private static async Task<IResult> StartAsync(HttpContext context,
        IOptions<OidcCodeOptions> settings, AdminCodeExchange exchange, PendingAdminLoginStore pending)
    {
        var query = context.Request.Query;
        if (query.TryGetValue("returnUrl", out var values) && values.Count != 1
            || AdminLoginReturnTarget.Normalize(values.Count == 0 ? null : values[0]) is null)
            return VocabularyHttpResponse.BadRequest("The login return target is invalid.");
        var metadata = await exchange.GetMetadataAsync(context.RequestAborted);
        if (metadata.InvalidConfiguration) return VocabularyHttpResponse.ServiceUnavailable("Hosted authentication is not configured.");
        if (metadata.AuthorizationEndpoint is null) return VocabularyHttpResponse.BadGateway("The authentication provider is unavailable.");
        var transaction = pending.Create(values.Count == 0 ? null : values[0], context.RequestAborted);
        if (transaction is null) return VocabularyHttpResponse.ServiceUnavailable("Hosted authentication is temporarily unavailable.");
        context.Response.Cookies.Append(transaction.CookieName, transaction.BrowserBinding, PendingAdminLoginCookie.ForStart(transaction));
        return Results.Redirect(QueryHelpers.AddQueryString(metadata.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.Value.ClientId,
            ["redirect_uri"] = settings.Value.RedirectUri,
            ["scope"] = settings.Value.Scope,
            ["state"] = transaction.State,
            ["nonce"] = transaction.Nonce,
            ["code_challenge"] = transaction.Challenge,
            ["code_challenge_method"] = "S256"
        }));
    }

    private static async Task<IResult> CallbackAsync(HttpContext context,
        IOptions<IdentityServiceOptions> identity, PendingAdminLoginStore pending, AdminCodeExchange exchange, IAdminSessionSignIn signIn)
    {
        try
        {
            var query = context.Request.Query;
            if (!One(query, "state", out var state) || !PendingAdminLoginStore.Canonical(state)
                || !One(query, "iss", out var issuer) || issuer != identity.Value.Issuer
                || query.ContainsKey("code") == query.ContainsKey("error")) return Failure("sign_in_failed");
            string? code = null;
            string? error = null;
            if (query.ContainsKey("code"))
            {
                if (!One(query, "code", out code) || !PendingAdminLoginStore.Canonical(code)) return Failure("sign_in_failed");
            }
            else if (!One(query, "error", out error) || error is not ("access_denied" or "invalid_request" or "invalid_scope"
                or "unauthorized_client" or "unsupported_response_type" or "server_error" or "temporarily_unavailable"))
                return Failure("sign_in_failed");
            var cookieName = PendingAdminLoginCookie.Name(state!);
            context.Request.Cookies.TryGetValue(cookieName, out var binding);
            var transaction = pending.Consume(state, binding, context.RequestAborted);
            if (transaction is null) return Failure("sign_in_failed");
            context.Response.Cookies.Delete(cookieName, PendingAdminLoginCookie.Attributes());
            if (error is not null) return Failure(error == "access_denied" ? "canceled"
                : error is "server_error" or "temporarily_unavailable" ? "provider_unavailable" : "sign_in_failed");
            var result = await exchange.RedeemAsync(code!, transaction.Verifier, transaction.Nonce, context.RequestAborted);
            if (result.Status != AdminCodeStatus.Success) return Failure(result.Status switch
            {
                AdminCodeStatus.Forbidden => "denied",
                AdminCodeStatus.Unavailable => "provider_unavailable",
                _ => "sign_in_failed"
            });
            await signIn.SignInAsync(context, result.Principal!, result.AccessToken!, result.IdToken, context.RequestAborted);
            return Results.Redirect(transaction.ReturnTarget);
        }
        catch (Exception exception) when (exception is AdminSessionStorageException or StorageBusyException)
        {
            // The existing storage middleware owns the fixed 500/503 and unknown-commit semantics.
            throw;
        }
        catch (OperationCanceledException) { return Failure("sign_in_failed"); }
        catch (Exception)
        {
            // Never pass provider, query or token exceptions to the generic exception logger.
            return Failure("sign_in_failed");
        }
    }

    private static bool One(IQueryCollection query, string key, out string? value)
    {
        value = null;
        if (!query.TryGetValue(key, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0])) return false;
        value = values[0];
        return true;
    }
    private static IResult Failure(string reason) => Results.Redirect("/#/login?reason=" + reason);
}

// Hosting diagnostics run before application middleware and log the full query at
// Information. These categories must remain off even with provider-specific Trace rules.
public sealed class AdminHostedLoginSafety(RequestDelegate next)
{
    public static bool SuppressLogCategory(string? category) => category is "Microsoft.AspNetCore.Hosting.Diagnostics"
        or "Microsoft.AspNetCore.Http.Result.RedirectResult"
        || category?.StartsWith("Microsoft.AspNetCore.HttpLogging", StringComparison.Ordinal) == true;

    public Task InvokeAsync(HttpContext context)
    {
        // Key the safety headers off the endpoint routing selected rather than the raw
        // path: case and trailing-slash variants of these routes run the same endpoint
        // and must carry the same response guarantees.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (route is "/admin/auth/start" or "/admin/auth/callback")
        {
            context.Response.Headers.CacheControl = "no-store";
            if (route == "/admin/auth/callback") context.Response.Headers["Referrer-Policy"] = "no-referrer";
        }
        // The logout response can carry the one-time upstream logout URI, and the
        // fixed return route always redirects with a one-time state, so neither may
        // be stored; the return route also hides its target from referrers like the
        // login callback.
        if (route == "/admin/auth/logout/return")
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
        }
        else if (route == "/admin/auth/logout")
        {
            context.Response.Headers.CacheControl = "no-store";
        }
        return next(context);
    }
}
