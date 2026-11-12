using System.Text.Json;
using Lexarbor.Host.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ServiceMantle.Web.Management;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The hosted administrator login surface on the framework's OpenID Connect handler: this host
/// maps the sign-in start, the antiforgery-token issuer, the session status, the logout and the
/// legacy failure-page redirect under the repository's historical <c>/admin/auth</c> prefix,
/// while the handler itself intercepts the callback and the sign-out callback on their
/// configured paths. The start endpoint alone carries the configured admin-login rate limit,
/// the logout endpoint alone carries the session-storage failure metadata, and the session
/// endpoint alone gains the ServiceMantle administrator policy. An unconfigured deployment
/// keeps every route mounted with its fixed degraded answer.
/// </summary>
public static class AdminAuthEndpoints
{
    public const string Prefix = "/admin/auth";

    public static IEndpointConventionBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.ServiceProvider.GetRequiredService<AdminOidcSettings>();
        var group = app.MapGroup(Prefix);
        if (settings.Available)
        {
            group.MapGet("/start", Start).RequireRateLimiting(RateLimitingExtensions.AdminLoginPolicy);
        }
        else
        {
            group.MapGet("/start", DegradedStart).RequireRateLimiting(RateLimitingExtensions.AdminLoginPolicy);
            // The protocol routes keep their fixed anonymous answers in the unconfigured
            // mode too (there is no handler to intercept them): a callback attempt lands on
            // the fixed sign-in failure, a sign-out return on the fixed failed-logout
            // redirect. In the configured mode the OpenID Connect handler intercepts both
            // paths before any endpoint runs.
            group.MapGet("/callback", DegradedCallback);
            group.MapGet("/logout/return", DegradedLogoutReturn);
        }
        group.MapGet("/csrf", Csrf);
        group.MapGet("/session", Session)
            .RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName);
        // The logout is the local-session-first flow in every mode: an unconfigured
        // deployment still revokes the presented session locally and answers the fixed
        // local-only result (no end-session URL can be built without an authority).
        group.MapPost("/logout", Logout).Add(builder => builder.Metadata.Add(new AdminSessionLogoutMetadata()));
        group.MapGet("/signin-failed", SignInFailedPage);
        return group;
    }

    /// <summary>
    /// Starts the authorization-code flow. The strict start-input contract is kept from the
    /// replaced slice: only a single-valued <c>returnUrl</c> is accepted and its value must be
    /// one of the allowlisted SPA routes (or the fixed application-root default when absent).
    /// The authority's Discovery document is fetched and its endpoint triple verified against
    /// the issuer's origin before the challenge, so a tampered document answers the fixed 502
    /// here instead of ever redirecting a browser to a second host.
    /// </summary>
    private static async Task Start(HttpContext context)
    {
        string? target = null;
        var values = context.Request.Query["returnUrl"];
        if (values.Count > 1
            || (values.Count == 1 && (target = AdminLoginReturnTarget.Normalize(values[0])) is null))
        {
            await AdminHostedLoginWriter.WriteStartFailureAsync(
                context, AdminStartFailure.InvalidReturnUrl, context.RequestAborted);
            return;
        }

        try
        {
            await EnsureTrustedAuthorityAsync(context, context.RequestAborted);
            await context.ChallengeAsync(AdminAuthenticationSource.OidcScheme, new AuthenticationProperties
            {
                RedirectUri = target ?? "/"
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            await AdminHostedLoginWriter.WriteStartFailureAsync(
                context, AdminStartFailure.AuthorityUnreachable, context.RequestAborted);
        }
    }

    /// <summary>
    /// Issues the antiforgery credential pair: the cookie is set alongside the token, and
    /// issuance is a public, unauthenticated read — the protection is the cookie binding, not
    /// secrecy of the issuer. The declared shape is kept: <c>{"token": "..."}</c> with
    /// <c>no-store</c>, in the unconfigured mode exactly like a configured one.
    /// </summary>
    private static IResult Csrf(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            """{"token":""" + JsonSerializer.Serialize(tokens.RequestToken) + "}",
            "application/json");
    }

    /// <summary>
    /// Reports the authorized administrator identity's fixed status shape: the hosted
    /// session's own principal when one is presented (the cookie is its subject), and the
    /// verified Bearer identity otherwise — the historical answer for every administrator.
    /// Never a token.
    /// </summary>
    private static async Task Session(HttpContext context)
    {
        var session = await context.AuthenticateAsync(AdminAuthenticationSource.SessionScheme);
        await AdminHostedLoginWriter.WriteSessionStatusAsync(
            context, session.Succeeded ? session.Principal! : context.User, context.RequestAborted);
    }

    /// <summary>
    /// The local-session-first logout with the standard RP-initiated upstream redirect:
    /// <list type="bullet">
    /// <item>A missing or wrong antiforgery credential answers the fixed 400
    /// <c>{"outcome":"csrf_rejected"}</c> and touches nothing.</item>
    /// <item>Without a live session there is nothing to revoke locally and no ID token to hint:
    /// the fixed local-only envelope.</item>
    /// <item>With one, the local session is revoked first (its audit row joins the revocation
    /// transaction), and only the request that actually ended the live session may hand the
    /// browser the authority's end-session URL — built from the server-held ID token, the
    /// registered post-logout URI and the protected state. A logout whose upstream redirect
    /// cannot be constructed keeps the local revocation and answers the fixed local-only
    /// envelope.</item>
    /// </list>
    /// </summary>
    private static async Task Logout(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            // A missing or wrong token changes nothing: no session is touched and no upstream
            // redirect is attempted. The fixed answer is indistinguishable for both defect
            // shapes.
            await AdminHostedLoginWriter.WriteLogoutCsrfRejectedAsync(context);
            return;
        }

        var session = await context.AuthenticateAsync(AdminAuthenticationSource.SessionScheme);
        if (!session.Succeeded)
        {
            // No live session means no local revocation and no end-session redirect: the fixed
            // local-only result, exactly as the package answered it.
            await AdminHostedLoginWriter.WriteLogoutLocalOnlyAsync(context);
            return;
        }

        var idTokenHint = session.Properties?.GetTokenValue("id_token");
        // The local sign-out runs first and is never rolled back: the server-side ticket is
        // removed with its audit row and the cookie deleted. The live-revocation marker tells
        // this request whether it was the one that ended the session — a concurrent logout that
        // lost the race answers the fixed local-only result instead of preparing a second
        // upstream redirect.
        await context.SignOutAsync(AdminAuthenticationSource.SessionScheme);
        if (!context.Items.ContainsKey(AdminAuthenticationSource.RevokedLiveSessionItemKey)
            || string.IsNullOrEmpty(idTokenHint))
        {
            await AdminHostedLoginWriter.WriteLogoutLocalOnlyAsync(context);
            return;
        }

        string? logoutUrl = null;
        try
        {
            logoutUrl = await BuildEndSessionUrlAsync(context, idTokenHint!, context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
        }

        if (logoutUrl is null)
        {
            await AdminHostedLoginWriter.WriteLogoutLocalOnlyAsync(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await AdminHostedLoginWriter.WriteLogoutPreparedAsync(context, logoutUrl);
    }

    /// <summary>
    /// The fixed degraded start answer of the unconfigured mode.
    /// </summary>
    private static async Task DegradedStart(HttpContext context)
    {
        await AdminHostedLoginWriter.WriteStartFailureAsync(
            context, AdminStartFailure.NotConfigured, context.RequestAborted);
    }

    /// <summary>
    /// The unconfigured mode's fixed callback answer: no handshake can exist, so every
    /// attempt lands on the bounded sign-in failure redirect.
    /// </summary>
    private static IResult DegradedCallback(HttpContext context)
    {
        AdminHostedLoginWriter.ApplyCallbackSafetyHeaders(context.Response);
        return Results.Redirect("/#/login?reason=sign_in_failed", permanent: false, preserveMethod: false);
    }

    /// <summary>
    /// The unconfigured mode's fixed sign-out return answer: no logout could have been
    /// prepared, so the fixed failed-logout redirect stands.
    /// </summary>
    private static IResult DegradedLogoutReturn(HttpContext context)
    {
        AdminHostedLoginWriter.ApplyCallbackSafetyHeaders(context.Response);
        return Results.Redirect(AdminHostedLoginWriter.LogoutFailedTarget, permanent: false, preserveMethod: false);
    }

    /// <summary>
    /// Lexarbor presents sign-in failures through the SPA's login route, never through a
    /// server-rendered failure page; the mapped page exists only for a direct visit.
    /// </summary>
    private static IResult SignInFailedPage()
    {
        return Results.Redirect("/#/login", permanent: false, preserveMethod: false);
    }

    /// <summary>
    /// Fetches the authority's Discovery document through the handler's own configuration
    /// manager and verifies the endpoint triple (authorization, token, JWKS) sits on the
    /// verified issuer's origin — the same-origin guarantee of the replaced slice, which the
    /// framework handler does not enforce by itself. A transport failure or a tampered document
    /// surfaces as the fixed provider-unavailable answer before any browser redirect.
    /// </summary>
    private static async Task EnsureTrustedAuthorityAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AdminAuthenticationSource.OidcScheme);
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        if (!IsSameOrigin(configuration.Issuer, configuration.AuthorizationEndpoint)
            || !IsSameOrigin(configuration.Issuer, configuration.TokenEndpoint)
            || !IsSameOrigin(configuration.Issuer, configuration.JwksUri))
        {
            throw new InvalidOperationException(
                "The authority's Discovery document publishes endpoints outside the issuer's origin.");
        }
    }

    /// <summary>
    /// Builds the standard RP-initiated end-session URL: the Discovery
    /// <c>end_session_endpoint</c>, the server-held ID token as <c>id_token_hint</c>, the
    /// registered post-logout URI, and the protected state that lands the browser on the fixed
    /// signed-out SPA result. Without a configured post-logout URI the URL carries the hint
    /// only — the fixed return route then cannot complete. An authority that publishes no
    /// end-session endpoint degrades logout to the fixed local-only result.
    /// </summary>
    private static async Task<string?> BuildEndSessionUrlAsync(HttpContext context, string idTokenHint,
        CancellationToken cancellationToken)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AdminAuthenticationSource.OidcScheme);
        // The unconfigured mode wires no authority, so no end-session URL exists to build:
        // the logout stays the fixed local-only result.
        if (options.ConfigurationManager is null)
        {
            return null;
        }

        var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
        if (string.IsNullOrEmpty(configuration.EndSessionEndpoint))
        {
            return null;
        }

        var settings = context.RequestServices.GetRequiredService<AdminOidcSettings>();
        var message = new OpenIdConnectMessage
        {
            EnableTelemetryParameters = false,
            IssuerAddress = configuration.EndSessionEndpoint,
            IdTokenHint = idTokenHint,
            PostLogoutRedirectUri = settings.PostLogoutRedirectUri
        };
        if (settings.PostLogoutRedirectUri is not null)
        {
            message.State = options.StateDataFormat.Protect(new AuthenticationProperties
            {
                RedirectUri = AdminHostedLoginWriter.PostLogoutReturnTarget
            });
        }

        return message.CreateLogoutRequestUrl();
    }

    private static bool IsSameOrigin(string? issuer, string? endpoint)
    {
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(endpoint))
        {
            return false;
        }

        return Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            && string.Equals(
                issuerUri.GetLeftPart(UriPartial.Authority),
                endpointUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase);
    }
}
