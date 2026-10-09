using System.Security.Claims;
using System.Security.Cryptography;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;
using ServiceMantle;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.Relational.DataProtection;
using ServiceMantle.Persistence.Relational.Stores;
using ServiceMantle.Web;
using ServiceMantle.Web.Management;
using SignaCore.Client.AspNetCore;
using KestrelBadHttpRequestException = Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException;

// Before anything is built. The container HEALTHCHECK runs this same assembly,
// and a health probe that first composed configuration, opened the database and
// started a web host would be reporting on the process it just created rather
// than on the one already serving.
if (args.Contains(HealthCheckCommand.Argument))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

PersistentConfigurationFile? persistentConfiguration = null;
if (PersistentConfigurationBootstrapper.IsRunningInContainer())
{
    persistentConfiguration = PersistentConfigurationBootstrapper.EnsureFile(
        builder.Environment.ContentRootPath);
    builder.Configuration.AddJsonFile(
        persistentConfiguration.Path,
        optional: false,
        reloadOnChange: false);

    // Keep the standard .NET precedence: explicit deployment settings override
    // the persisted file, and the persisted file overrides the image defaults.
    builder.Configuration.AddEnvironmentVariables();
    if (args.Length > 0)
    {
        builder.Configuration.AddCommandLine(args);
    }
}

// HTTP listen port is hardcoded to 5008 (not configurable via ASPNETCORE_URLS).
// Host port mapping is controlled by scripts/start.sh: -p ${Port}:5008.
const int httpPort = 5008;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });
});

// Add services to the container.
var connectionString = SqliteConnectionConfiguration.Build(
    builder.Configuration.GetConnectionString("Default"),
    builder.Environment.ContentRootPath);
// One registration path serves both the application and the ServiceMantle
// Data Protection key repository: the factory creates the repository's own
// contexts, and the scoped wrapper gives each request exactly one context.
builder.Services.AddDbContextFactory<VocabularyDbContext>(options =>
{
    options.UseSqlite(connectionString);
});
builder.Services.AddScoped(serviceProvider =>
    serviceProvider.GetRequiredService<IDbContextFactory<VocabularyDbContext>>().CreateDbContext());
// The ServiceMantle startup gate: SQLite target preparation, single-instance
// validation, and migration orchestration around the executor above.
builder.Services.AddLexarborDatabaseStartup();
// Data Protection keys live in the SQLite database as `sm:v1:` authenticated
// envelopes scoped to the lexarbor service id, protected by the deployment's
// root key (injected or created under data/); see DataProtectionRootKey.
builder.Services
    .AddDataProtection()
    .SetApplicationName("Lexarbor")
    .PersistKeysToServiceMantleEfCore<VocabularyDbContext>(
        ServiceId.Parse("lexarbor"),
        serviceProvider => DataProtectionRootKey.Resolve(
            serviceProvider.GetRequiredService<IConfiguration>(),
            serviceProvider.GetRequiredService<IHostEnvironment>().ContentRootPath));
// Framework error diagnostics can include the XML element being processed.
// Emit only our safe startup diagnostic; never send key material to host logs.
builder.Services.AddLogging(logging =>
    logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.None));

builder.Services.AddScoped<IVocabularyRepository, VocabularyRepository>();
builder.Services.AddScoped<IVocabularyBookRepository, VocabularyBookRepository>();
builder.Services.AddScoped<IVocabularyMeaningRepository, VocabularyMeaningRepository>();
builder.Services.AddScoped<IVocabularyBookUnitRepository, VocabularyBookUnitRepository>();
builder.Services.AddScoped<IVocabularyMeaningUnitRepository, VocabularyMeaningUnitRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddSingleton(TimeProvider.System);
// A session database failure must not emit SQL, parameters, or provider exception details.
builder.Services.PostConfigure<LoggerFilterOptions>(options =>
{
    // Preserve all configured category/provider thresholds, including more-specific
    // Database.Command rules, while preventing sensitive diagnostics during session IO.
    options.Rules.Insert(0, new LoggerFilterRule(null, null, options.MinLevel, null));
    for (var index = 0; index < options.Rules.Count; index++)
    {
        var rule = options.Rules[index];
        options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
            (provider, category, level) =>
                !LexarborLoggingSetup.SuppressLogCategory(category)
                && !(AdminSessionRepository.IsSessionOperation
                    && category?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true)
                && (rule.Filter?.Invoke(provider, category, level) ?? true));
    }
});
builder.Services.AddScoped<AdminSessionRepository>();
builder.Services.AddScoped<AdminSessionStore>();
// Management audit rows share the scoped VocabularyDbContext, so a staged audit write joins
// whatever unit of work the caller owns (ServiceMantle 0.2.1 has no auto-registration helper).
builder.Services.AddScoped<IManagementAuditWriter, EfCoreManagementAuditWriter<VocabularyDbContext>>();
builder.Services.AddScoped<AdminAuthenticationAudit>();
builder.Services.AddScoped<IVocabularyWordEditRepository, VocabularyWordEditRepository>();
builder.Services.AddScoped<VocabularyWordEditService>();
builder.Services.AddScoped<VocabularyMeaningEditService>();
builder.Services.AddScoped<IVocabularyAdminQueryRepository, VocabularyAdminQueryRepository>();
builder.Services.AddScoped<VocabularyAdminQueryService>();
builder.Services.AddScoped<IVocabularyCleanupRepository, VocabularyCleanupRepository>();
builder.Services.AddScoped<VocabularyCleanupService>();
builder.Services.AddScoped<VocabularyDomainService>();
builder.Services.AddScoped<VocabularyBookDomainService>();
builder.Services.AddScoped<VocabularyBookUnitDomainService>();
builder.Services.Configure<RouteHandlerOptions>(options =>
{
    options.ThrowOnBadRequest = true;
});

builder.Services.Configure<IdentityServiceOptions>(
    builder.Configuration.GetSection(IdentityServiceOptions.SectionName));
// The retired password proxy left one live setting behind: AdminAuthentication:Provider.
// It must be unset (or exactly OidcCode); anything else fails startup rather than
// letting an operator believe password login still exists. Validated on start so a
// test host's configuration overrides are seen exactly like a deployment's.
builder.Services.AddOptions<AdminAuthenticationOptions>()
    .Configure(builder.Configuration.GetSection(AdminAuthenticationOptions.SectionName).Bind)
    .Validate(options =>
        string.IsNullOrWhiteSpace(options.Provider) ||
        string.Equals(options.Provider, "OidcCode", StringComparison.OrdinalIgnoreCase),
        "AdminAuthentication:Provider no longer selects a login provider. Password proxy login " +
        "(Oidc password grant and Gateway) has been removed; the hosted SignaCore login page is " +
        "the only administrator sign-in. Remove AdminAuthentication:Provider (or set it to " +
        "\"OidcCode\"), register a Confidential application with your identity provider, configure " +
        "AdminAuthentication:OidcCode, and see docs/development/HostedLoginReleaseNotes.md for the " +
        "upgrade steps. The LEXARBOR_OIDC_*, LEXARBOR_GATEWAY_* and LEXARBOR_COOKIE_SECURE " +
        "environment variables no longer exist.")
    .Validate(options => string.IsNullOrWhiteSpace(options.HttpTestOrigins),
        AdminAuthenticationOptions.RemovedSettingFailureMessage)
    .ValidateOnStart();

builder.Services.AddScoped<AdminAccessTokenValidator>();
// The hosted-login protocol settings, read from the historical keys and re-bound onto the
// official client package: the container environment contract is unchanged.
builder.Services.Configure<OidcCodeOptions>(builder.Configuration.GetSection(OidcCodeOptions.SectionName));
builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
// Registered ahead of the package so its TryAdd registration defers to the SQLite-backed
// store: the ticket rows, the audit companion writes and the v1 protected payload are
// Lexarbor's own persistence boundary. The self registration keeps one instance behind
// both the package's ITicketStore seam and this assembly's own lookups.
builder.Services.AddSingleton<AdminSessionTicketStore>();
builder.Services.AddSingleton<ITicketStore>(serviceProvider =>
    serviceProvider.GetRequiredService<AdminSessionTicketStore>());
builder.Services.AddSingleton<AdminHostedLoginResponseWriter>();
builder.Services.AddSingleton<AdminPreSignInRoleGate>();
builder.Services.AddSignaCoreHostedLogin(login =>
{
    var identity = builder.Configuration.GetSection(IdentityServiceOptions.SectionName).Get<IdentityServiceOptions>()
        ?? new IdentityServiceOptions();
    var code = builder.Configuration.GetSection(OidcCodeOptions.SectionName).Get<OidcCodeOptions>()
        ?? new OidcCodeOptions();
    // A deployment that has not configured the hosted login (all three client settings
    // blank) keeps the historical placeholder authority out of the protocol options, so
    // the optional-login degradation answers /admin/auth/start with its fixed 503 the
    // way the retired implementation did; any configured client setting makes the
    // authority a required, validated option — half-configuration fails startup.
    var loginConfigured = !string.IsNullOrWhiteSpace(code.ClientId)
        || !string.IsNullOrWhiteSpace(code.ClientSecret)
        || !string.IsNullOrWhiteSpace(code.RedirectUri);
    login.Authority = loginConfigured ? identity.Authority : null;
    login.ClientId = code.ClientId;
    login.ClientSecret = code.ClientSecret;
    login.RedirectUri = code.RedirectUri;
    login.Scope = code.Scope;
    login.PostLogoutRedirectUri = string.IsNullOrWhiteSpace(code.PostLogoutRedirectUri)
        ? null
        : code.PostLogoutRedirectUri;
    // The session cookie name follows the redirect URI's scheme, the same rule the
    // official client applies to its whole cookie profile (0.1.16): an https redirect
    // keeps the retired implementation's byte-for-byte name — existing handles and the
    // protected payload format survive the upgrade and a rollback either way — while
    // an http redirect drops the __Host- prefix a plain-HTTP deployment cannot satisfy
    // (the prefix demands Secure), which the package's validator would otherwise
    // refuse at startup.
    login.SessionCookieName = AdminSessionCookie.ForRedirectUri(code.RedirectUri);
    // Hosted login stays optional at startup: an unconfigured deployment keeps serving its
    // public API, /admin/auth/start answers its historical 503, and a configured but illegal
    // value still fails startup — the package's missing-versus-illegal split.
    login.AllowUnconfiguredStartup = true;
    login.ReturnUrlValidator = AdminLoginReturnTarget.Normalize;
    login.SessionEndpointRequireAuthorization = true;
    login.PostLogoutReturnPath = AdminHostedLoginResponseWriter.PostLogoutReturnTarget;
});
// The Lexarbor presentation and the pre-sign-in administrator gate are singleton services
// wired onto the package options after configuration, so the request-scoped audit writer is
// resolved per failing request rather than captured here.
builder.Services.AddOptions<SignaCoreHostedLoginOptions>()
    .PostConfigure<AdminHostedLoginResponseWriter, AdminPreSignInRoleGate>(
        (login, writer, gate) =>
        {
            login.ResponseWriter = writer;
            login.PreSignInAuthorizationDecision = gate;
        });

builder.Services
    .AddAuthentication(AdminAuthenticationSource.PolicyScheme)
    .AddPolicyScheme(AdminAuthenticationSource.PolicyScheme, null, options =>
    {
        options.ForwardDefaultSelector = context => AdminAuthenticationSource.Select(context.Request);
        // The management API's fixed JSON 401/403 answers: the official session handler's
        // 302-to-start challenge never reaches an API surface through this scheme.
        options.ForwardChallenge = AdminAuthenticationSource.PresentationScheme;
        options.ForwardForbid = AdminAuthenticationSource.PresentationScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationPresentationHandler>(
        AdminAuthenticationSource.PresentationScheme, null)
    .AddJwtBearer();

// Configured from the resolved options rather than from the configuration read
// above, for the reason given at the provider switch: a value a test host
// supplies is not on builder.Configuration yet, so the issuer, audience and
// metadata settings this scheme trusts could not be exercised by a test. A
// deployment sees no difference -- appsettings, the persisted file, environment
// variables and the command line are all composed before this runs -- but the
// settings are now the ones that actually took effect.
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<IdentityServiceOptions>, IHostEnvironment>(
        (options, identity, environment) =>
        {
            var identityService = identity.Value;
            options.Authority = identityService.Authority;
            options.Audience = identityService.Audience;
            options.RequireHttpsMetadata =
                RequiresHttpsMetadata(identityService, environment);
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = identityService.Issuer,
                ValidateAudience = true,
                ValidAudience = identityService.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RoleClaimType = ClaimTypes.Role,
                NameClaimType = ClaimTypes.Name
            };
            options.Events = new JwtBearerEvents
            {
                // Bearer tokens come only from the Authorization header: the retired
                // password-login cookie is never read here again.
                OnTokenValidated = context =>
                {
                    // A Bearer credential is a non-interactive caller: the
                    // ServiceMantle management identity is minted from the token's
                    // own verified claims, and any servicemantle.* claim the token
                    // carried is removed first, so it can never elevate.
                    ManagementIdentityMapping.Apply(
                        context.Principal!,
                        context.HttpContext.RequestServices
                            .GetRequiredService<IOptionsMonitor<AdminAuthenticationOptions>>()
                            .CurrentValue.RequiredRole,
                        WellKnownManagementAuditOperatorSources.ServiceAccount.Value);
                    return Task.CompletedTask;
                },
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    if (!context.Response.HasStarted)
                    {
                        await VocabularyHttpResponse.WriteFailureAsync(
                            context.Response,
                            StatusCodes.Status401Unauthorized,
                            "Authentication is required.");
                    }
                },
                OnForbidden = async context =>
                {
                    if (!context.Response.HasStarted)
                    {
                        await VocabularyHttpResponse.WriteFailureAsync(
                            context.Response,
                            StatusCodes.Status403Forbidden,
                            "Administrator role is required.");
                    }
                }
            };
        });
// The administrator policy is the ServiceMantle management contract: it grants
// only a principal whose ServiceMantle claims (minted by
// ManagementIdentityMapping from the verified session or Bearer identity)
// resolve to one operator holding management.admin. The role check itself stays
// Lexarbor's product rule and lives in the mapping.
builder.Services.AddServiceMantleManagementAuthorization();

var networkOptions = builder.Configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new NetworkOptions();
var useTrustedForwarding = networkOptions.IsConfigured || networkOptions.ForwardLimit != 1;

// The ServiceMantle host identity is registered unconditionally: capabilities
// built on it (Problem Details correlation, security headers, redacted logging,
// Data Protection keys, health endpoints) must be available in every
// deployment, with or without a trusted proxy. Forwarded-header trust remains
// the one capability that is added only when an operator configures it.
var instanceIdText = builder.Configuration["Service:InstanceId"];
InstanceId instanceId;
if (string.IsNullOrEmpty(instanceIdText))
{
    // An unset (or explicitly empty) instance id keeps the historical behavior
    // of a fresh random id per start.
    instanceId = InstanceId.Parse($"lexarbor-{Guid.NewGuid():N}");
}
else if (InstanceId.TryParse(instanceIdText, out var configuredInstanceId))
{
    // TryParse's out parameter is nullable by signature only; a true result
    // always carries a parsed, normalized value.
    instanceId = configuredInstanceId!;
}
else
{
    // The invalid value is deliberately absent from the message: an instance
    // id is operator-supplied text, and echoing it would put it in logs and
    // error reports, where it does not belong. Whitespace-only text is invalid
    // rather than "unset" — InstanceId trims and then rejects an empty result.
    throw new InvalidOperationException(
        "Service:InstanceId is not a valid instance identifier. It must be 1 to 256 characters "
        + "after trimming, with no control characters. Remove the setting to go back to a random "
        + "instance id per start.");
}

var serviceMantle = builder.Services.AddServiceMantle(
    ServiceId.Parse("lexarbor"),
    instanceId,
    serviceVersion: ApplicationVersion.Current);
// Readiness is answered by the ServiceMantle health endpoints: one bounded,
// read-only SQLite probe per request plus this process's startup migration
// result. The probe timeout stays well inside the container HEALTHCHECK's own
// deadline so a wedged database is reported rather than killed.
serviceMantle.AddServiceMantleHealthEndpoints(options => options.ProbeTimeout = TimeSpan.FromSeconds(3));
// The mandatory security response-header baseline for the administration
// surface. The capability is deliberately not configurable; the /admin
// endpoints opt in through the shared route-group requirement below.
serviceMantle.AddSecurityResponseHeaders();
var healthState = new LexarborHealthState();
builder.Services.AddSingleton(healthState);
builder.Services.AddSingleton<ServiceMantle.Health.IServiceHealthSnapshotSource>(
    serviceProvider => new LexarborHealthSnapshotSource(
        healthState,
        serviceProvider.GetRequiredService<IServiceScopeFactory>()));
// Business exceptions map to HTTP through the ServiceMantle Problem Details
// pipeline: a fixed type/title/status/errorCode (plus correlation id) per
// mapping, with no room for exception text, SQL or credentials to reach a
// response. The titles are deliberately generic — the per-case domain reasons
// stay in server logs, not in public responses. Endpoint-explicit failures
// keep the existing envelope; only exception-generated responses change shape.
serviceMantle.AddExceptionMapping<DomainValidationException>(
    StatusCodes.Status400BadRequest, "vocabulary.validation", "The request is invalid.");
serviceMantle.AddConditionalExceptionMapping<BadHttpRequestException>(
[
    // Kestrel reports a body over the endpoint's size limit this way; ordered so
    // the 413 case wins before the generic bad-request fallback.
    new(413, "vocabulary.request_too_large", "The request body is too large.",
        exception => exception.StatusCode == StatusCodes.Status413PayloadTooLarge),
    new(400, "vocabulary.bad_request", "The request is invalid.")
]);
// Kestrel rejects oversized bodies with its own derived BadHttpRequestException
// whose exact type the registry matches separately from the public base class.
// The type is marked obsolete in favor of the public base class, but Kestrel
// still throws it, so the mapping targets it deliberately.
#pragma warning disable CS0618 // Type or member is obsolete
serviceMantle.AddConditionalExceptionMapping<KestrelBadHttpRequestException>(
[
    new(413, "vocabulary.request_too_large", "The request body is too large.",
        exception => exception.StatusCode == StatusCodes.Status413PayloadTooLarge),
    new(400, "vocabulary.bad_request", "The request is invalid.")
]);
#pragma warning restore CS0618 // Type or member is obsolete
serviceMantle.AddExceptionMapping<ResourceNotFoundException>(
    StatusCodes.Status404NotFound, "vocabulary.not_found", "The requested resource was not found.");
serviceMantle.AddExceptionMapping<ConflictException>(
    StatusCodes.Status409Conflict, "vocabulary.conflict", "The request conflicts with existing data.");
serviceMantle.AddExceptionMapping<BusinessRuleException>(
    StatusCodes.Status422UnprocessableEntity, "vocabulary.business_rule", "The request violates a business rule.");
serviceMantle.AddConditionalExceptionMapping<StorageBusyException>(
[
    // The one caller-retryable storage failure keeps its bounded Retry-After;
    // the library clamps the value to a safe range.
    new(503, "vocabulary.storage_busy", "The vocabulary database is temporarily busy.",
        retryAfterSeconds: 1)
]);

// Console logging runs through the ServiceMantle Serilog pipeline: every
// structured property is sanitized before it reaches the console, and the
// service identity travels with each event. The `Logging:LogLevel` section is
// mapped onto the pipeline: `Default` becomes the minimum level, every other
// category becomes a minimum-level override, and `None` — which an override
// cannot express — keeps its LoggerFilterOptions rule below.
var mappedLogLevels = LexarborLoggingSetup.Map(builder.Configuration.GetSection("Logging:LogLevel"));
builder.AddServiceMantleSerilog(options =>
{
    options.MinimumLevel = mappedLogLevels.MinimumLevel;
    options.MinimumLevelOverrides = mappedLogLevels.Overrides.Count > 0
        ? new Dictionary<string, LogLevel>(mappedLogLevels.Overrides, StringComparer.Ordinal)
        : null;
});
foreach (var category in mappedLogLevels.NoneLevelCategories)
{
    builder.Logging.AddFilter(category, LogLevel.None);
}

// The built-in denied list (Authorization, Proxy-Authorization, Cookie,
// Set-Cookie, X-Api-Key, X-Auth-Token) already covers every header Lexarbor
// handles: its own additions — the X-SignaCore-CSRF antiforgery marker and the
// test-only client-address header — carry no secret material, so there is
// nothing to register beyond wiring the sanitizer itself.
serviceMantle.AddSensitiveHeaders();
if (useTrustedForwarding)
{
    serviceMantle.AddForwardedHeaders(options =>
    {
        options.KnownProxies = networkOptions.TrustedProxies;
        options.KnownIPNetworks = networkOptions.TrustedNetworks;
        options.ForwardLimit = networkOptions.ForwardLimit;
    });
}
// Optional OpenTelemetry, default off: with neither signal enabled nothing
// is registered — no provider, no exporter, no outbound connection — and the
// process behaves exactly as before. The resource carries only the ServiceMantle
// identity (service.name, service.version, service.instance.id).
var telemetryTracesEnabled = builder.Configuration.GetValue("Telemetry:Otlp:Traces:Enabled", false);
var telemetryMetricsEnabled = builder.Configuration.GetValue("Telemetry:Otlp:Metrics:Enabled", false);
if (telemetryTracesEnabled || telemetryMetricsEnabled)
{
    serviceMantle.AddOpenTelemetryInstrumentation(options =>
    {
        options.EnableAspNetCoreTracing = telemetryTracesEnabled;
        options.EnableHttpClientTracing = telemetryTracesEnabled;
        options.EnableRuntimeMetrics = telemetryMetricsEnabled;
    });
    serviceMantle.AddOpenTelemetryOtlpExporter(options =>
    {
        MapOtlpSignal(builder.Configuration.GetSection("Telemetry:Otlp:Traces"), options.Traces);
        MapOtlpSignal(builder.Configuration.GetSection("Telemetry:Otlp:Metrics"), options.Metrics);
        options.Traces.Enabled = telemetryTracesEnabled;
        options.Metrics.Enabled = telemetryMetricsEnabled;
    });
    if (telemetryTracesEnabled)
    {
        // The callback and logout-return URLs carry one-time code/state values;
        // no span attribute may export them.
        builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
            tracing.AddProcessor(new AdminCallbackQueryRedactionProcessor()));
    }

    // The header name is configuration; the value comes from the environment
    // only, through this resolver, and never reaches logs or exceptions.
    builder.Services.AddSingleton<ServiceMantle.Diagnostics.IRemoteTelemetryAuthenticationResolver>(
        new LexarborOtlpAuthenticationResolver());
}

static void MapOtlpSignal(IConfiguration section, ServiceMantle.Diagnostics.Export.Otlp.OtlpSignalOptions signal)
{
    if (Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint))
    {
        signal.Endpoint = endpoint;
    }

    if (bool.TryParse(section["AllowInsecureLoopbackForTesting"], out var loopback))
    {
        signal.AllowInsecureLoopbackForTesting = loopback;
    }

    signal.AuthenticationHeaderName = section["AuthenticationHeaderName"];
    if (Enum.TryParse<ServiceMantle.Diagnostics.Export.Otlp.OtlpProtocol>(section["Protocol"], ignoreCase: true, out var protocol))
    {
        signal.Protocol = protocol;
    }
}

serviceMantle.AddLexarborRateLimiting(builder.Configuration);

var app = builder.Build();



app.Logger.LogInformation("Lexarbor starting, version {Version}", ApplicationVersion.Current);
app.Logger.LogInformation(
    "Lexarbor build, channel {Channel}, revision {Revision}",
    ApplicationVersion.Channel,
    ApplicationVersion.Revision ?? "unknown");
app.Logger.LogInformation("Listening: http://+:{Port}", httpPort);
if (persistentConfiguration != null)
{
    app.Logger.LogInformation(
        persistentConfiguration.Created
            ? "Created persistent configuration from image defaults at {ConfigurationPath}"
            : "Loaded persistent configuration from {ConfigurationPath}",
        persistentConfiguration.Path);
}
app.Logger.LogInformation("Database: SQLite");

var rateLimitOptions = app.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value;
LogRateLimit("admin login", rateLimitOptions.AdminLogin);
LogRateLimit("public API", rateLimitOptions.PublicApi);
if (useTrustedForwarding)
{
    app.Logger.LogInformation(
        "Trusting forwarded client addresses from {ProxyCount} proxy address(es) and {NetworkCount} network(s), {ForwardLimit} hop(s) deep",
        networkOptions.TrustedProxies.Count,
        networkOptions.TrustedNetworks.Count,
        networkOptions.ForwardLimit);
}
else
{
    // Not a warning: a container with its port published directly sees the real
    // client address and this is correct. It is logged because the alternative,
    // a reverse proxy with no trusted hop configured, looks identical from
    // inside and collapses every client into one rate limit partition.
    app.Logger.LogInformation(
        "Rate limits partition on the connecting address. Behind a reverse proxy, set Network:TrustedProxies or Network:TrustedNetworks or every client will share one partition.");
}

// Checked at startup rather than left to the first request that needs it: a
// metadata address the bearer scheme refuses is a failure to start, not a 500 on
// every administration request while the deployment reports itself healthy.
var effectiveIdentityOptions = app.Services
    .GetRequiredService<IOptions<IdentityServiceOptions>>().Value;
if (RequiresHttpsMetadata(effectiveIdentityOptions, app.Environment))
{
    // Checked here rather than left to the bearer scheme, which raises the same
    // refusal but names a property instead of the setting an operator would
    // change.
    if (!string.IsNullOrWhiteSpace(effectiveIdentityOptions.Authority) &&
        !effectiveIdentityOptions.Authority.StartsWith(
            "https://",
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"IdentityService:Authority is '{effectiveIdentityOptions.Authority}', which does not use HTTPS. " +
            "The signing keys published there decide every administration authorization, so anyone able to " +
            "rewrite that response can issue itself an administrator token. Configure an https authority, or " +
            "set IdentityService:RequireHttpsMetadata to false to accept this one.");
    }

    app.Logger.LogInformation(
        "Identity signing metadata is required over HTTPS from {Authority}",
        effectiveIdentityOptions.Authority);
}
else
{
    // Logged at warning for the same reason a disabled rate limit is: the keys
    // served from this address decide every administration authorization, so a
    // caller able to rewrite the response can issue itself an administrator
    // token. That is acceptable against a local provider and nowhere else, and
    // it must not be a quiet state.
    app.Logger.LogWarning(
        "Identity signing metadata is accepted over plain HTTP from {Authority}. Anyone able to rewrite that response can mint an administrator token. Point IdentityService:Authority at an https address for any provider that is not on this host.",
        effectiveIdentityOptions.Authority);
}

void LogRateLimit(string name, RateLimitPolicyOptions policy)
{
    if (policy.Enabled)
    {
        app.Logger.LogInformation(
            "Rate limit for {Policy}: {PermitLimit} requests per {WindowSeconds}s per client address",
            name,
            policy.PermitLimit,
            policy.WindowSeconds);
    }
    else
    {
        app.Logger.LogWarning("Rate limit for {Policy} is disabled by configuration", name);
    }
}

if (!app.Environment.IsDevelopment() &&
    !app.Environment.IsEnvironment("Testing"))
{
    // Hosted login is optional at startup exactly like the password proxy was: the
    // public API keeps serving and /admin/auth/start answers its existing 503. The
    // official client reports the one thing an operator can fix through that answer.
    if (string.IsNullOrWhiteSpace(app.Configuration["AdminAuthentication:OidcCode:ClientId"])
        || string.IsNullOrWhiteSpace(app.Configuration["AdminAuthentication:OidcCode:ClientSecret"])
        || string.IsNullOrWhiteSpace(app.Configuration["AdminAuthentication:OidcCode:RedirectUri"]))
    {
        app.Logger.LogError(
            "Administrator login is not configured because AdminAuthentication:OidcCode is missing. The service will continue running.");
    }
}

// Configure database initialization. The outcome feeds the readiness snapshot:
// a failed migration stops startup (and records the failure for any future
// non-fatal handling), while a disabled initialization only checks once whether
// migrations are pending so readiness still reflects the real schema state. The
// key repository needs the service_data_protection_keys table, so the startup
// probe follows the migration (or, with initialization disabled, the schema
// must already exist).
if (builder.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    try
    {
        // The ServiceMantle startup gate: observe (and only now create) the
        // SQLite target, refuse unusable ones — with one WAL crash-recovery
        // attempt — and run the EF migration orchestration. A failure keeps
        // its fixed safe diagnostic and stops startup with a non-zero exit.
        await LexarborDatabaseStartup.RunStartupMigrationAsync(app.Services);
        healthState.MigrationStatus = ServiceMantle.Health.ServiceMigrationReadinessState.Succeeded;
    }
    catch
    {
        healthState.MigrationStatus = ServiceMantle.Health.ServiceMigrationReadinessState.Failed;
        throw;
    }
}
else
{
    // One read-only check whether migrations are pending. A database that
    // cannot even be opened (for example a placeholder file a deployment
    // pre-mounted) leaves the host running and readiness honestly not-ready;
    // with initialization disabled the schema is the operator's responsibility.
    try
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var pending = await dbContext.Database.GetPendingMigrationsAsync();
        healthState.MigrationStatus = pending.Any()
            ? ServiceMantle.Health.ServiceMigrationReadinessState.NotStarted
            : ServiceMantle.Health.ServiceMigrationReadinessState.Succeeded;
    }
    catch (Exception exception) when (
        exception is OperationCanceledException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
    {
        healthState.MigrationStatus = ServiceMantle.Health.ServiceMigrationReadinessState.NotStarted;
    }
}

// The key repository fails closed: a wrong root key, damaged ciphertext, an
// unreadable root-key file or an unusable database stops startup here with a
// fixed safe diagnostic — never key material or provider detail — instead of
// surfacing as a 500 on the first administrator login. The probe runs only
// once the startup schema check verified the database: with initialization
// disabled and migrations pending (or the file unreadable), the schema is the
// operator's responsibility, the host keeps serving with readiness honestly
// not-ready, and the first administrator login still fails safe against an
// unusable repository.
if (healthState.MigrationStatus == ServiceMantle.Health.ServiceMigrationReadinessState.Succeeded)
{
    try
    {
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Lexarbor.AdminKeys.StartupProbe.v1");
        const string probe = "lexarbor-key-storage-probe";
        if (protector.Unprotect(protector.Protect(probe)) != probe)
        {
            throw new CryptographicException();
        }
    }
    catch (Exception exception) when (
        exception is DataProtectionKeyRepositoryException
            or InvalidOperationException
            or CryptographicException)
    {
        app.Logger.LogCritical("{Diagnostic}", DataProtectionRootKey.StartupFailureMessage);
        await app.DisposeAsync();
        return 1;
    }
}

// Configure the HTTP request pipeline.
if (useTrustedForwarding)
{
    // First, so that everything downstream — the rate limiter above all — sees
    // the client's address rather than the proxy's.
    //
    // ServiceMantle validates the immutable trust boundary at startup.
    app.UseServiceMantleForwardedHeaders();
}

// Correlation ids come before everything that can fail so every response —
// problem details included — can be tied back to one request.
app.UseServiceMantleCorrelationId();
// Every routed /admin response — success, validation, 401/403, 429, exception —
// carries the ServiceMantle mandatory security-header baseline while its
// headers are still unsent. The endpoints opt in below via the shared
// requirement; /api, /health and the SPA never match it.
app.UseServiceMantleSecurityResponseHeaders();
// Replaces the hand-written Vocabulary exception middleware: ServiceMantle
// writes application/problem+json with the mappings registered above.
app.UseServiceMantleProblemDetails();
app.UseDefaultFiles();
app.UseStaticFiles();
// Ahead of authentication so a rejected caller costs a partition lookup rather
// than a JWT validation, which for a cookie-bearing request can reach out to the
// identity provider for signing keys.
app.UseLexarborRateLimitRetryAfter();
app.UseRateLimiter();
app.UseMiddleware<AdminSessionFailureMiddleware>();
app.UseAuthentication();
// The session-identity bridge: a hosted-login session principal receives its
// ServiceMantle management identity here, and the retired password-login JWT cookie
// is cleaned up at the hosted boundary events. Session-authenticated unsafe methods
// carry the official antiforgery boundary inside the session scheme itself.
app.UseMiddleware<AdminSessionIdentityMiddleware>();
app.UseAuthorization();
// One requirement for the whole administration surface. The empty-prefix group
// changes no route; it only carries the ServiceMantle security response-header
// marker down to every endpoint and nested group these mappers create, so the
// six mandatory headers apply uniformly — including the unknown-route catch-all
// below. The one mapper that also maps public /api endpoints takes the
// requirement for its admin group as a delegate instead, so /api never
// inherits it.
var adminSurface = app.MapGroup(string.Empty).RequireServiceMantleSecurityResponseHeaders();
adminSurface.MapAdminAuthEndpoints();
adminSurface.MapSystemVersionEndpoints();
adminSurface.MapVocabularyWordEditEndpoints();
adminSurface.MapVocabularyMeaningEditEndpoints();
adminSurface.MapVocabularyBookUnitEndpoints();
adminSurface.MapVocabularyMeaningPositionEndpoints();
adminSurface.MapVocabularyAdminQueryEndpoints();
adminSurface.MapVocabularyCleanupEndpoints();
app.MapVocabularyHttpEndpoints(
    rateLimitOptions.PublicApi.Enabled ? RateLimitingExtensions.PublicApiPolicy : null,
    static adminGroup => adminGroup.RequireServiceMantleSecurityResponseHeaders());
// The ServiceMantle health endpoints are anonymous and unmetered: /health/live
// answers liveness alone, /health/ready and /health project the readiness
// snapshot. Build identity stays in the startup logs and the authorized
// administrator version endpoint.
app.MapServiceMantleHealthEndpoints();

string[] allHttpMethods =
[
    HttpMethods.Get,
    HttpMethods.Post,
    HttpMethods.Put,
    HttpMethods.Patch,
    HttpMethods.Delete,
    HttpMethods.Options
];
var unknownApi = app.MapMethods(
        "/api/{**path}",
        allHttpMethods,
        () => VocabularyHttpResponse.NotFound("API endpoint was not found."));
if (rateLimitOptions.PublicApi.Enabled)
{
    unknownApi.RequireRateLimiting(RateLimitingExtensions.PublicApiPolicy);
}
else
{
    unknownApi.DisableRateLimiting();
}
adminSurface.MapMethods(
        "/admin/{**path}",
        allHttpMethods,
        () => VocabularyHttpResponse.NotFound("Admin endpoint was not found."))
    .RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName);
app.MapFallbackToFile("index.html").AllowAnonymous();

// The service identity (ServiceName, ServiceVersion, InstanceId) rides a
// factory-wide logging scope around the whole run, so startup logs and every
// request log carry the same identity fields. Per-request correlation ids are
// layered on top when the correlation middleware runs.
using (app.Services.GetRequiredService<ServiceMantle.Web.Logging.ServiceLogContext>()
    .BeginScope(app.Logger))
{
    try
    {
        app.Run();
    }
    catch (OptionsValidationException exception)
    {
        // Startup validation (the AdminAuthentication:Provider tripwire above) reports
        // through this path: a critical log naming the setting and a non-zero exit —
        // the same shape the key-ring validation uses. Disposing here is deliberately
        // skipped: process exit reclaims everything, and tearing the partially started
        // host down inside this handler can hang a container indefinitely.
        app.Logger.LogCritical("{Diagnostic}", exception.Message);
        return 1;
    }
}

// Reached when the host shuts down. Present because the health check path above
// returns a status, which makes this an int-returning entry point.
return 0;

/// <summary>
/// Whether the identity provider's signing metadata may only be fetched over
/// HTTPS. The previous value was a hardcoded false with no way for a deployment
/// to say otherwise.
/// </summary>
static bool RequiresHttpsMetadata(
    IdentityServiceOptions identityService,
    IHostEnvironment environment)
{
    if (identityService.RequireHttpsMetadata.HasValue)
    {
        return identityService.RequireHttpsMetadata.Value;
    }

    if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
    {
        return false;
    }

    // What the requirement protects is a network path an attacker could rewrite,
    // and loopback is not one. Exempting it also keeps a container that has not
    // been given an identity provider starting and serving its public API: the
    // image's placeholder authority is http://localhost:8080, and refusing to
    // start over an unconfigured administration login would be a harsher answer
    // than the one absent provider credentials already get, which is to log and
    // return 503 from the login endpoint.
    return !(Uri.TryCreate(identityService.Authority, UriKind.Absolute, out var authority)
             && authority.IsLoopback);
}

public partial class Program
{
}
