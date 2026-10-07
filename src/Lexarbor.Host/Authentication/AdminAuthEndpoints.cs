using System.Security.Claims;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ServiceMantle.Web.Management;

namespace Lexarbor.Host.Authentication;

public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHostedAdminLogin();
        app.MapGet("/admin/auth/session", GetSession)
            .RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName);
        // Deliberately unlimited. Logout revokes a local session, and an administrator
        // who cannot end a session because someone else exhausted a shared ceiling
        // is a worse outcome than the requests this would have refused.
        app.MapPost("/admin/auth/logout", LogoutAsync)
            .WithMetadata(new AdminSessionLogoutMetadata())
            .AllowAnonymous();
        // The fixed, pre-registered return route of the prepared logout. Anonymous
        // and unlimited like the login callback: it consumes a one-time
        // browser-bound state and answers only with fixed in-site redirects, so it
        // establishes no session, accepts no external target, and echoes no input.
        app.MapGet("/admin/auth/logout/return", LogoutReturnAsync)
            .AllowAnonymous();
        return app;
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
        AdminSessionStore store,
        AdminAuthenticationAudit audit,
        AdminPreparedLogout preparedLogout,
        PendingAdminLogoutStore pendingLogouts,
        HostedLoginHttpTestTransport httpTestTransport,
        CancellationToken cancellationToken)
    {
        context.Request.Cookies.TryGetValue(AdminSessionCookie.EffectiveName(httpTestTransport), out var handle);
        // The logout audit row joins the revocation's transaction: it is staged only for a
        // live revoked session, and commits or rolls back with that revocation.
        var snapshot = await store.RevokeAndReadAsync(handle, audit.StageLogout(context), cancellationToken);
        AdminSessionCookie.Clear(context, httpTestTransport);
        // The local session ends first, and every caller without a live
        // snapshot — no session, an expired or repeated logout —
        // gets the unchanged idempotent 200. Only the single concurrent caller that
        // atomically revoked a session holding a real ID token can prepare
        // the upstream logout; nobody else calls upstream or fabricates a hint.
        if (string.IsNullOrWhiteSpace(snapshot?.IdToken))
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
                PendingAdminLogoutCookie.ForStart(httpTestTransport, transaction));
        return VocabularyHttpResponse.Ok(new { logoutUrl = logoutUri });
    }

    private static IResult LogoutReturnAsync(
        HttpContext context,
        PendingAdminLogoutStore pendingLogouts,
        HostedLoginHttpTestTransport httpTestTransport,
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
            context.Request.Cookies.TryGetValue(PendingAdminLogoutCookie.Name(httpTestTransport, state), out var binding);
            if (pendingLogouts.Consume(state, binding, cancellationToken))
            {
                context.Response.Cookies.Delete(
                    PendingAdminLogoutCookie.Name(httpTestTransport, state),
                    PendingAdminLogoutCookie.Attributes(httpTestTransport));
                return Results.Redirect(LoggedOutTarget);
            }
        }
        return Results.Redirect(LogoutFailedTarget);
    }
}
