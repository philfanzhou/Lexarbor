using System.Security.Claims;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The only anonymous endpoint that costs anything to serve: it forwards
        // the submitted credentials to the identity provider, so without a ceiling
        // it is both a password-guessing oracle and a way to point traffic at that
        // provider from an address the provider attributes to Lexarbor.
        app.MapPost("/admin/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingExtensions.AdminLoginPolicy);
        app.MapHostedAdminLogin();
        app.MapGet("/admin/auth/session", GetSession)
            .RequireAuthorization("VocabularyAdmin");
        // Deliberately unlimited. Logout revokes a local session, and an administrator
        // who cannot end a session because someone else exhausted a shared ceiling
        // is a worse outcome than the requests this would have refused.
        app.MapPost("/admin/auth/logout", LogoutAsync)
            .WithMetadata(new AdminSessionLogoutMetadata())
            .AllowAnonymous();
        // The fixed, pre-registered return route of the Code-mode prepared logout.
        // Anonymous and unlimited like the login callback: it consumes a one-time
        // browser-bound state and answers only with fixed in-site redirects, so it
        // establishes no session, accepts no external target, and echoes no input.
        app.MapGet("/admin/auth/logout/return", LogoutReturnAsync)
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> LoginAsync(
        AdminLoginRequest request,
        HttpContext context,
        IAdminCredentialAuthenticator authenticator,
        AdminAccessTokenValidator accessTokenValidator,
        IOptions<AdminAuthenticationOptions> authenticationOptions,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return VocabularyHttpResponse.BadRequest("Username and password are required.");
        }

        var adminAuthentication = authenticationOptions.Value;
        if (!authenticator.IsConfigured &&
            !environment.IsDevelopment() &&
            !environment.IsEnvironment("Testing"))
        {
            return VocabularyHttpResponse.ServiceUnavailable(
                "Administrator login is not configured.");
        }

        var result = await authenticator.AuthenticateAsync(
            request.Username,
            request.Password,
            cancellationToken);
        if (result.Status == AdminCredentialStatus.InvalidCredentials)
        {
            return VocabularyHttpResponse.Unauthorized("Invalid username or password.");
        }

        if (result.Status != AdminCredentialStatus.Success ||
            string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return VocabularyHttpResponse.BadGateway(
                "The authentication provider is unavailable.");
        }

        var principal = await accessTokenValidator.ValidateAsync(
            result.AccessToken,
            cancellationToken);
        if (principal == null)
        {
            return VocabularyHttpResponse.BadGateway(
                "The authentication provider returned an invalid access token.");
        }

        if (!VocabularyClaims.HasRole(principal, adminAuthentication.RequiredRole))
        {
            return VocabularyHttpResponse.Forbidden("Administrator role is required.");
        }

        context.Response.Cookies.Append(
            adminAuthentication.CookieName,
            result.AccessToken,
            CreateCookieOptions(
                adminAuthentication,
                result.ExpiresIn ?? TimeSpan.FromHours(1)));

        // Every field here comes from the validated token. Whatever the provider claimed
        // about the user in its own response envelope is never echoed back.
        return VocabularyHttpResponse.Ok(new
        {
            username = VocabularyClaims.GetDisplayName(principal) ?? string.Empty,
            roles = VocabularyClaims.GetRoles(principal)
        });
    }

    private static IResult GetSession(ClaimsPrincipal user)
    {
        var username = VocabularyClaims.GetDisplayName(user) ?? string.Empty;
        var roles = VocabularyClaims.GetRoles(user);
        return VocabularyHttpResponse.Ok(new { username, roles });
    }

    private const string LoggedOutTarget = "/#/login?reason=logged_out";
    private const string LogoutFailedTarget = "/#/login?reason=logout_failed";

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IOptions<AdminAuthenticationOptions> authenticationOptions,
        AdminSessionStore store,
        AdminPreparedLogout preparedLogout,
        PendingAdminLogoutStore pendingLogouts,
        CancellationToken cancellationToken)
    {
        context.Request.Cookies.TryGetValue(AdminSessionCookie.Name, out var handle);
        var snapshot = await store.RevokeAndReadAsync(handle, cancellationToken);
        AdminSessionCookie.Clear(context, authenticationOptions.Value);
        // The local session ends first in every mode, and every caller without a live
        // snapshot — no session, a legacy JWT cookie, an expired or repeated logout —
        // gets the unchanged idempotent 200. Only the single concurrent caller that
        // atomically revoked a Code-mode session holding a real ID token can prepare
        // the upstream logout; nobody else calls upstream or fabricates a hint.
        if (authenticationOptions.Value.Provider != AdminAuthenticationProvider.OidcCode
            || string.IsNullOrWhiteSpace(snapshot?.IdToken))
            return VocabularyHttpResponse.Ok();

        // The return redirect is optional upstream: it is sent only when the deployed
        // URI is valid and a one-time browser-bound state could be created. An
        // exhausted state store degrades to a return-less upstream logout instead of
        // failing the local one.
        var postLogoutRedirectUri = preparedLogout.ValidPostLogoutRedirectUri();
        var transaction = postLogoutRedirectUri is null ? null : pendingLogouts.Create(cancellationToken);
        string? logoutUri;
        try
        {
            logoutUri = await preparedLogout.RequestAsync(
                snapshot!.IdToken!,
                transaction is null ? null : postLogoutRedirectUri,
                transaction?.State,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The local revocation is committed and never rolls back; the upstream
            // outcome is unknown, is never retried, and is never claimed.
            return VocabularyHttpResponse.Ok();
        }
        // Without a verified upstream URI the envelope keeps its plain 200 shape:
        // in hosted mode a missing logoutUrl means local-only logout, and the UI
        // must then say that the provider may still hold a session. No upstream
        // error text is ever echoed.
        if (logoutUri is null) return VocabularyHttpResponse.Ok();
        // The transaction cookie is written only for the success that can actually
        // return; the browser sees only the verified one-time handle inside it.
        if (transaction is not null)
            context.Response.Cookies.Append(
                transaction.CookieName,
                transaction.BrowserBinding,
                PendingAdminLogoutCookie.ForStart(transaction));
        return VocabularyHttpResponse.Ok(new { logoutUrl = logoutUri });
    }

    private static IResult LogoutReturnAsync(
        HttpContext context,
        PendingAdminLogoutStore pendingLogouts,
        CancellationToken cancellationToken)
    {
        // The provider appends the stored state byte-for-byte to the registered
        // return URI. Anything other than exactly one canonical, unconsumed state
        // bound to this browser ends in the same fixed failed redirect: no echo, no
        // external target, no session, and no cookie but its own is ever touched.
        if (context.Request.Query.TryGetValue("state", out var states)
            && states.Count == 1
            && states[0] is { Length: > 0 } state
            && PendingAdminLogoutStore.Canonical(state))
        {
            context.Request.Cookies.TryGetValue(PendingAdminLogoutCookie.Name(state), out var binding);
            if (pendingLogouts.Consume(state, binding, cancellationToken))
            {
                context.Response.Cookies.Delete(
                    PendingAdminLogoutCookie.Name(state),
                    PendingAdminLogoutCookie.Attributes());
                return Results.Redirect(LoggedOutTarget);
            }
        }
        return Results.Redirect(LogoutFailedTarget);
    }

    internal static CookieOptions CreateCookieOptions(
        AdminAuthenticationOptions options,
        TimeSpan? maxAge)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = maxAge
        };
    }

    public sealed class AdminLoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
