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
        var start = app.MapGet("/admin/auth/start", StartAsync).AllowAnonymous();
        if (app.ServiceProvider.GetRequiredService<IOptions<RateLimitOptions>>().Value.AdminLogin.Enabled)
        {
            start.RequireRateLimiting(RateLimitingExtensions.AdminLoginPolicy);
        }
        else
        {
            start.DisableRateLimiting();
        }
        app.MapGet("/admin/auth/callback", CallbackAsync).AllowAnonymous();
    }

    private static async Task<IResult> StartAsync(HttpContext context,
        IOptions<OidcCodeOptions> settings, AdminCodeExchange exchange, PendingAdminLoginStore pending,
        HostedLoginHttpTestTransport httpTestTransport)
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
        context.Response.Cookies.Append(transaction.CookieName, transaction.BrowserBinding, PendingAdminLoginCookie.ForStart(httpTestTransport, transaction));
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
        IOptions<IdentityServiceOptions> identity, PendingAdminLoginStore pending, AdminCodeExchange exchange,
        IAdminSessionSignIn signIn, AdminAuthenticationAudit audit, HostedLoginHttpTestTransport httpTestTransport)
    {
        try
        {
            var query = context.Request.Query;
            if (!One(query, "state", out var state) || !PendingAdminLoginStore.Canonical(state)
                || !One(query, "iss", out var issuer) || issuer != identity.Value.Issuer
                || query.ContainsKey("code") == query.ContainsKey("error")) return await FailureAsync(context, audit, "sign_in_failed");
            string? code = null;
            string? error = null;
            if (query.ContainsKey("code"))
            {
                if (!One(query, "code", out code) || !PendingAdminLoginStore.Canonical(code)) return await FailureAsync(context, audit, "sign_in_failed");
            }
            else if (!One(query, "error", out error) || error is not ("access_denied" or "invalid_request" or "invalid_scope"
                or "unauthorized_client" or "unsupported_response_type" or "server_error" or "temporarily_unavailable"))
                return await FailureAsync(context, audit, "sign_in_failed");
            var cookieName = PendingAdminLoginCookie.Name(httpTestTransport, state!);
            context.Request.Cookies.TryGetValue(cookieName, out var binding);
            var transaction = pending.Consume(state, binding, context.RequestAborted);
            if (transaction is null) return await FailureAsync(context, audit, "sign_in_failed");
            context.Response.Cookies.Delete(cookieName, PendingAdminLoginCookie.Attributes(httpTestTransport));
            if (error is not null) return await FailureAsync(context, audit, error == "access_denied" ? "canceled"
                : error is "server_error" or "temporarily_unavailable" ? "provider_unavailable" : "sign_in_failed");
            var result = await exchange.RedeemAsync(code!, transaction.Verifier, transaction.Nonce, context.RequestAborted);
            if (result.Status != AdminCodeStatus.Success) return await FailureAsync(context, audit, result.Status switch
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
        catch (OperationCanceledException)
        {
            // A cancelled request is not a completed security event: no session was committed
            // and no audit row is written for it.
            return Failure("sign_in_failed");
        }
        catch (Exception)
        {
            // Never pass provider, query or token exceptions to the generic exception logger.
            return await FailureAsync(context, audit, "sign_in_failed");
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

    /// <summary>
    /// Records the fixed-reason failed login audit row before answering with the unchanged
    /// failure redirect. The standalone audit save follows the session storage failure
    /// semantics, so an audit save failure surfaces as the fixed 500/503 instead of silently
    /// losing the security event behind a successful redirect.
    /// </summary>
    private static async Task<IResult> FailureAsync(HttpContext context, AdminAuthenticationAudit audit, string reason)
    {
        await audit.RecordLoginFailedAsync(context, reason, context.RequestAborted);
        return Failure(reason);
    }
}
