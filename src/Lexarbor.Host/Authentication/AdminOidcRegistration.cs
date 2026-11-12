using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// Registers the admin hosted-login slice on the framework's own OpenID Connect handler,
/// replacing the retired <c>SignaCore.Client.AspNetCore</c> client package wholesale. The
/// shared-framework handler owns the authorization-code flow, the correlation/nonce browser
/// binding and the standard RP-initiated end-session redirect; this host keeps every
/// Lexarbor-owned boundary around it: the SQLite-backed server-side session tickets with their
/// audit companion writes, the antiforgery write model on the historical
/// <c>X-SignaCore-CSRF</c> header, the pre-sign-in administrator gate on the verified access
/// token, the fixed JSON/redirect presentation, the return-target allowlist, and the
/// Bearer/session scheme split — so the browser-facing contract of the replaced package
/// integration is unchanged.
/// </summary>
/// <remarks>
/// Every protocol value is applied inside the options pipeline, not at service registration:
/// a test host supplies configuration after this registration runs, and the deployment's
/// composed configuration is what the options build sees — the same reason the JwtBearer
/// trust resolves its values from options.
/// </remarks>
public static class AdminOidcRegistration
{
    public static IServiceCollection AddLexarborAdminOidc(
        this IServiceCollection services)
    {
        // Hosting diagnostics log raw query strings before the application middleware runs,
        // including sign-in callbacks rejected by the correlation checks. The framework's
        // OpenID Connect handler also logs the full protocol message URLs (authorization code,
        // state, and tokens included) at Debug level; the slice's security posture keeps every
        // protocol value out of host logs, so both categories are silenced here — a stance the
        // replaced package took for its own protocol surfaces.
        services.AddLogging(logging =>
        {
            logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.None);
        });
        services.AddOptions<AdminOidcSettings>()
            .Configure<IConfiguration>((settings, composed) => settings.Load(composed))
            .ValidateOnStart();
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AdminOidcSettings>>().CurrentValue);

        // The antiforgery pair of the write model is public in every mode: an unconfigured
        // deployment still issues token pairs and still answers logout with its fixed
        // local-only result, exactly like the package's optional-login degradation. The
        // historical request-header name stays byte for byte: the SPA echoes it on every write.
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AdminAuthenticationSource.CsrfCookieName;
            options.HeaderName = AdminAuthenticationSource.CsrfHeaderName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
        });
        services.AddOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>()
            .Configure<AdminOidcSettings>((options, settings) =>
                options.Cookie.SecurePolicy = settings.PlainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always);
        // Wrap the framework's antiforgery with the boundary's historical user-neutrality:
        // the default implementation binds each request token to the minting request's
        // principal, which would retire a pair the SPA fetched before signing in. The cookie
        // binding and every cryptographic check stay exactly the framework's own.
        var frameworkAntiforgery = services.FirstOrDefault(
            descriptor => descriptor.ServiceType == typeof(Microsoft.AspNetCore.Antiforgery.IAntiforgery));
        if (frameworkAntiforgery?.ImplementationType is { } implementation)
        {
            services.RemoveAll<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
            services.AddSingleton<Microsoft.AspNetCore.Antiforgery.IAntiforgery>(serviceProvider =>
                new AdminUserNeutralAntiforgery(
                    (Microsoft.AspNetCore.Antiforgery.IAntiforgery)Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                        .CreateInstance(serviceProvider, implementation)));
        }

        // The backchannel carries the client secret and, through it, the authority's Discovery,
        // JWKS and token endpoints: it never follows a redirect, never carries a cookie, writes
        // no request/response log line, and normalizes client-side timeouts into transport
        // failures so a timeout keeps answering the fixed provider-unavailable outcome instead
        // of a 5xx. The ServiceMantle correlation propagation is stacked onto this same named
        // client in Program.cs (named-client configuration stacks).
        services.AddTransient<AdminOidcBackchannelTimeoutHandler>();
        services.AddHttpClient(AdminAuthenticationSource.OidcHttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            .AddHttpMessageHandler<AdminOidcBackchannelTimeoutHandler>()
            .RemoveAllLoggers();

        // The SQLite-backed server-side ticket store and its periodic expiry sweep (the
        // repository rows, the audit companion writes and the v1 protected payload format are
        // Lexarbor's own persistence boundary, unchanged by the handler swap).
        services.AddSingleton<AdminSessionTicketStore>();
        services.AddHostedService<AdminSessionCleanupService>();
        services.AddSingleton<AdminPreSignInRoleGate>();

        // Both schemes are registered in every mode so the request scheme selector always
        // resolves; the OpenID Connect handler stays inert in the unconfigured mode (no
        // callback paths, no authority) and the cookie session answers the fixed
        // re-authentication result for every presented cookie.
        services.AddAuthentication()
            .AddCookie(AdminAuthenticationSource.SessionScheme, options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.IsEssential = true;
                // The SPA navigates to the sign-in start explicitly, so the cookie handler's own
                // login redirect is never exercised and keeps its inert default. The ticket's
                // lifetime comes from the token response's expires_at (set on the ticket before
                // it reaches the store); the fallback span below is only a defensive bound.
                // Sessions never slide: an expired session answers the fixed re-authentication
                // result, never a silent refresh.
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.SlidingExpiration = false;
            })
            .AddOpenIdConnect(AdminAuthenticationSource.OidcScheme, options =>
            {
                options.ResponseType = OpenIdConnectResponseType.Code;
                // The code flow's standard response mode — and the only mode the authority
                // delivers. Query is also the mode whose response_mode parameter the handler
                // correctly omits from the authorize redirect for a code flow.
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                // The wire contract of the replaced slice: no x-client-SKU/x-client-ver
                // telemetry parameters on the authorize or end-session redirect.
                options.DisableTelemetry = true;
                options.SaveTokens = true;
                options.MapInboundClaims = false;
                options.SignInScheme = AdminAuthenticationSource.SessionScheme;
                options.BackchannelTimeout = TimeSpan.FromSeconds(30);
                // The authorization parameters travel in the redirect itself; a discovery entry
                // for pushed authorization must never be taken.
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                // The nonce and correlation carrier cookies must follow the configured cookie
                // profile: the framework defaults (SameSite=None with Secure=Always) are
                // refused by browsers under the plain-HTTP profile. Lax is enough — the
                // callback is a top-level navigation, exactly like the session cookie itself.
                // The five-minute lifetime matches the replaced slice's handshake window
                // (the framework's own default is fifteen minutes).
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                // The documented 30-second lifetime tolerance of the replaced integration,
                // shared with the Bearer trust.
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "role";

                // Strict authorization-response shape (kept from the replaced slice; the
                // framework handler itself accepts a weaker form): state/iss/code/error are
                // single-valued, the RFC 9207 issuer parameter is required, and it must equal
                // the discovery issuer — the mix-up protection of a single-authority client.
                // The raw query is the decision input: by this event the handler has already
                // replaced the message's state with its userstate portion.
                options.Events.OnMessageReceived = async context =>
                {
                    foreach (var member in new[] { "state", "iss", "code", "error" })
                    {
                        if (context.Request.Query[member].Count > 1)
                        {
                            context.Fail(AdminAuthenticationSource.InvalidResponseFailure);
                            return;
                        }
                    }

                    // A response without a state or an issuer parameter is rejected before any
                    // authority interaction; so is one whose state does not unprotect — the
                    // raw query is the decision input, because by this event the handler has
                    // already replaced the message's state with its userstate portion. A
                    // present issuer must then equal the discovery issuer, the mix-up
                    // protection of a single-authority client (the comparison uses the
                    // cached configuration of the challenge).
                    var issuer = context.ProtocolMessage.Iss;
                    var state = context.Request.Query["state"].ToString();
                    if (string.IsNullOrEmpty(state)
                        || string.IsNullOrEmpty(issuer)
                        || context.Options.StateDataFormat.Unprotect(state) is null)
                    {
                        context.Fail(AdminAuthenticationSource.InvalidResponseFailure);
                        return;
                    }

                    if (context.Options.ConfigurationManager is not { } manager
                        || await manager.GetConfigurationAsync(context.HttpContext.RequestAborted) is not { } discovered
                        || !string.Equals(issuer, discovered.Issuer, StringComparison.Ordinal))
                    {
                        context.Fail(AdminAuthenticationSource.InvalidResponseFailure);
                    }
                };

                // The pre-sign-in administrator gate (the package's PreSignInAuthorizationDecision
                // seam): a session may only be established for a subject whose re-verified
                // access token carries the configured administrator role — the SignaCore ID
                // token is a closed set without roles, so the gate reads the token-endpoint
                // response's access token through the host's JwtBearer trust. Failing
                // validation prevents the sign-in entirely — no ticket and no cookie are
                // written, and an existing session is neither replaced nor extended. An
                // unsigned ID token is refused here as well: the framework skips the signature
                // requirement for tokens received over its authenticated backchannel.
                options.Events.OnTokenValidated = async context =>
                {
                    if (string.Equals(context.SecurityToken?.SignatureAlgorithm, "none", StringComparison.Ordinal))
                    {
                        context.Fail(AdminAuthenticationSource.InvalidResponseFailure);
                        return;
                    }

                    // The validated identity omits the iss claim (the validator consumes it),
                    // so the verified issuer rides the properties into the session store.
                    if (context.Properties is { } validatedProperties && context.SecurityToken is { } token)
                    {
                        validatedProperties.Items[AdminAuthenticationSource.OidcIssuerPropertiesKey] = token.Issuer;
                    }

                    var gate = context.HttpContext.RequestServices.GetRequiredService<AdminPreSignInRoleGate>();
                    var accessToken = context.TokenEndpointResponse?.AccessToken;
                    var accessPrincipal = string.IsNullOrEmpty(accessToken)
                        ? null
                        : await context.HttpContext.RequestServices.GetRequiredService<AdminAccessTokenValidator>()
                            .ValidateAsync(accessToken, context.HttpContext.RequestAborted);
                    if (accessPrincipal is null
                        // The access token must carry the verified subject the session is
                        // issued for — the replaced slice demanded a non-empty one.
                        || string.IsNullOrEmpty(VocabularyClaims.GetSubject(accessPrincipal)))
                    {
                        // An access token that cannot be re-verified is a protocol failure,
                        // never a silent downgrade of the gate.
                        context.Fail(AdminAuthenticationSource.InvalidResponseFailure);
                    }
                    else if (!gate.AllowsSession(accessPrincipal))
                    {
                        context.Fail(AdminAuthenticationSource.NotAdminFailure);
                    }
                };

                // Every protocol failure lands on one bounded SPA outcome with the security
                // headers the routed surface gets from its endpoint markers; the failed-login
                // audit row is recorded first, skipped only for an already-cancelled request
                // (not a completed security event). No failure detail ever reaches the browser.
                options.Events.OnRemoteFailure = async context =>
                {
                    var reason = AdminHostedLoginWriter.CallbackReason(context.Failure);
                    if (!context.HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        await context.HttpContext.RequestServices.GetRequiredService<AdminAuthenticationAudit>()
                            .RecordLoginFailedAsync(context.HttpContext, reason, context.HttpContext.RequestAborted);
                    }

                    AdminHostedLoginWriter.ApplyCallbackSafetyHeaders(context.Response);
                    context.Response.Redirect("/#/login?reason=" + reason);
                    context.HandleResponse();
                };

                // A token response whose lifetime has already elapsed never creates a session:
                // the failure surfaces through the fixed sign-in failure landing. (The
                // ticket-received event cannot fail the flow — the framework ignores a Fail
                // result there — so the boundary is checked where it still can.)
                options.Events.OnTokenResponseReceived = context =>
                {
                    if (int.TryParse(
                            context.TokenEndpointResponse?.ExpiresIn,
                            System.Globalization.NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var seconds)
                        && seconds <= 0)
                    {
                        context.Fail("The token response is already expired.");
                    }

                    return Task.CompletedTask;
                };

                // The server-side session never outlives the issued access token, and the
                // cookie is persistent with the same boundary so a browser restart keeps
                // neither. The framework writes the final redirect only after this event, so
                // the security headers land on the completed response.
                options.Events.OnTicketReceived = context =>
                {
                    if (context.Properties is { } properties
                        && DateTimeOffset.TryParse(
                            properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var expiry))
                    {
                        properties.ExpiresUtc = expiry.ToUniversalTime();
                        properties.IsPersistent = true;
                    }

                    AdminHostedLoginWriter.ApplyCallbackSafetyHeaders(context.Response);
                    return Task.CompletedTask;
                };

                // The authorization redirect carries the configured callback URI exactly as
                // registered with the authority — never the request-derived one, which a
                // TLS-terminating proxy would rewrite to the internal scheme and host.
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    var settings = context.HttpContext.RequestServices.GetRequiredService<AdminOidcSettings>();
                    context.ProtocolMessage.RedirectUri = settings.RedirectUri;
                    return Task.CompletedTask;
                };

                // A sign-out callback whose state does not unprotect is not a completed logout:
                // answer the fixed failed-logout SPA landing instead of an empty 200. A valid
                // state keeps the framework's redirect to the protected landing and only
                // carries the same security headers here.
                options.Events.OnSignedOutCallbackRedirect = context =>
                {
                    AdminHostedLoginWriter.ApplyCallbackSafetyHeaders(context.Response);
                    // The strict response-shape rule of the sign-in callback applies to the
                    // sign-out callback as well: a duplicated state parameter is not a
                    // well-formed response, and the framework's message parsing takes the
                    // last value — so the check reads the raw query before anything else.
                    if (context.HttpContext.Request.Query["state"].Count > 1
                        || string.IsNullOrEmpty(context.Properties?.RedirectUri))
                    {
                        context.Response.Redirect(AdminHostedLoginWriter.LogoutFailedTarget);
                        context.HandleResponse();
                    }

                    return Task.CompletedTask;
                };
            });

        // The per-deployment protocol values — read from the resolved options, so a test
        // host's late configuration is exercised exactly like a deployment's composed one.
        services.AddOptions<CookieAuthenticationOptions>(AdminAuthenticationSource.SessionScheme)
            .Configure<AdminOidcSettings, TimeProvider, AdminSessionTicketStore>((options, settings, time, store) =>
            {
                options.Cookie.Name = AdminSessionCookie.ForRedirectUri(settings.RedirectUri);
                options.Cookie.SecurePolicy = settings.PlainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                options.SessionStore = store;
                options.TimeProvider = time;
            });
        services.AddOptions<OpenIdConnectOptions>(AdminAuthenticationSource.OidcScheme)
            .Configure<AdminOidcSettings, IHostEnvironment, TimeProvider, System.Net.Http.IHttpClientFactory>(
                (options, settings, environment, time, factory) =>
                {
                    options.TimeProvider = time;
                    options.Backchannel = factory.CreateClient(AdminAuthenticationSource.OidcHttpClientName);
                    if (!settings.Available)
                    {
                        // The unconfigured mode: no callback paths and no authority, so the
                        // handler stays inert while the cookie session keeps answering the
                        // fixed re-authentication result for every presented cookie. The
                        // placeholders keep the scheme's own options validation satisfied —
                        // the scheme is a request-handler scheme, so the handler initializes
                        // on every request and must stay constructible, but it is never
                        // challenged and never intercepts a path in this mode.
                        options.ClientId = "unavailable";
                        options.Authority = "http://localhost:1";
                        options.RequireHttpsMetadata = false;
                        return;
                    }

                    options.Authority = settings.Authority;
                    options.ClientId = settings.ClientId;
                    options.ClientSecret = settings.ClientSecret;
                    options.CallbackPath = AdminOidcSettings.CallbackPath;
                    options.SignedOutCallbackPath = AdminOidcSettings.LogoutReturnPath;
                    options.SignedOutRedirectUri = AdminHostedLoginWriter.PostLogoutReturnTarget;
                    options.Scope.Clear();
                    foreach (var scope in settings.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        options.Scope.Add(scope);
                    }
                    // Plain-HTTP authorities follow the same environment/loopback rule the
                    // Bearer trust uses: transport is a deployment decision made in front of
                    // the application.
                    options.RequireHttpsMetadata = RequiresHttpsMetadata(environment, settings);
                    options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
                    options.ProtocolValidator.NonceLifetime = TimeSpan.FromMinutes(5);
                    options.NonceCookie.SecurePolicy = settings.PlainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                    options.CorrelationCookie.SecurePolicy = settings.PlainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                });
        return services;
    }

    /// <summary>
    /// Whether the authority's signing metadata may only be fetched over HTTPS: the same rule
    /// the Bearer trust applies, so one deployment statement governs both trusts. Plain HTTP is
    /// a deployment decision made in front of the application.
    /// </summary>
    private static bool RequiresHttpsMetadata(IHostEnvironment environment, AdminOidcSettings settings)
    {
        if (settings.RequireHttpsMetadata.HasValue)
        {
            return settings.RequireHttpsMetadata.Value;
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return false;
        }

        return !(Uri.TryCreate(settings.Authority, UriKind.Absolute, out var authority)
                 && authority.IsLoopback);
    }
}
