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
        app.MapGet("/admin/auth/method", (IOptions<AdminAuthenticationOptions> options) =>
            VocabularyHttpResponse.Ok(new { method = options.Value.Provider == AdminAuthenticationProvider.OidcCode ? "hosted" : "password" })).AllowAnonymous();
        app.MapGet("/admin/auth/start", StartAsync).AllowAnonymous().RequireRateLimiting(RateLimitingExtensions.AdminLoginPolicy);
        app.MapGet("/admin/auth/callback", CallbackAsync).AllowAnonymous();
    }

    private static async Task<IResult> StartAsync(HttpContext context, IOptions<AdminAuthenticationOptions> admin,
        IOptions<OidcCodeOptions> settings, AdminCodeExchange exchange, PendingAdminLoginStore pending)
    {
        if (admin.Value.Provider != AdminAuthenticationProvider.OidcCode)
            return VocabularyHttpResponse.BadRequest("Hosted authentication is disabled.");
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

    private static async Task<IResult> CallbackAsync(HttpContext context, IOptions<AdminAuthenticationOptions> admin,
        IOptions<IdentityServiceOptions> identity, PendingAdminLoginStore pending, AdminCodeExchange exchange, IAdminSessionSignIn signIn)
    {
        if (admin.Value.Provider != AdminAuthenticationProvider.OidcCode) return Failure("sign_in_failed");
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
public sealed class AdminHostedLoginSafety(RequestDelegate next, IOptions<AdminAuthenticationOptions> options, EndpointDataSource endpoints)
{
    public static bool SuppressLogCategory(string? category) => category is "Microsoft.AspNetCore.Hosting.Diagnostics"
        or "Microsoft.AspNetCore.Http.Result.RedirectResult"
        || category?.StartsWith("Microsoft.AspNetCore.HttpLogging", StringComparison.Ordinal) == true;
    public Task InvokeAsync(HttpContext context)
    {
        // JSON content-type routing can select a fallback before rate limiting. In Code
        // mode only, restore the real login metadata so even malformed/non-JSON
        // submissions share the quota and reach the pre-binding rejection. Old
        // modes retain their original routing and JSON/error contracts unchanged.
        if (options.Value.Provider == AdminAuthenticationProvider.OidcCode
            && HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/admin/auth/login")
        {
            var login = endpoints.Endpoints.OfType<RouteEndpoint>().First(endpoint =>
                endpoint.RoutePattern.RawText == "/admin/auth/login"
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
            context.SetEndpoint(login);
        }
        if (context.Request.Path is var path && (path == "/admin/auth/start" || path == "/admin/auth/callback" || path == "/admin/auth/method"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (path == "/admin/auth/callback") context.Response.Headers["Referrer-Policy"] = "no-referrer";
        }
        return next(context);
    }
}

public sealed class HostedPasswordLoginMiddleware(RequestDelegate next, IOptions<AdminAuthenticationOptions> options)
{
    public Task InvokeAsync(HttpContext context) => options.Value.Provider == AdminAuthenticationProvider.OidcCode
        && HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/admin/auth/login"
        ? VocabularyHttpResponse.WriteFailureAsync(context.Response, StatusCodes.Status400BadRequest,
            "Password login is disabled for hosted authentication.")
        : next(context);
}

public sealed class HostedCredentialAuthenticator(AdminCodeExchange exchange) : IAdminCredentialAuthenticator
{
    public bool IsConfigured => exchange.IsConfigured;
    public Task<AdminCredentialResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
        => Task.FromResult(AdminCredentialResult.Unavailable);
}
