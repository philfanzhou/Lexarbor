using System.Security.Claims;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Authentication.Providers;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

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
var connectionString = BuildSqliteConnectionString(
    builder.Configuration.GetConnectionString("Default"),
    builder.Environment.ContentRootPath);
builder.Services.AddDbContext<VocabularyDbContext>(options =>
{
    options.UseSqlite(connectionString);
});

builder.Services.AddScoped<IVocabularyRepository, VocabularyRepository>();
builder.Services.AddScoped<IVocabularyBookRepository, VocabularyBookRepository>();
builder.Services.AddScoped<IVocabularyMeaningRepository, VocabularyMeaningRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<VocabularyDomainService>();
builder.Services.AddScoped<VocabularyBookDomainService>();
builder.Services.Configure<RouteHandlerOptions>(options =>
{
    options.ThrowOnBadRequest = true;
});

builder.Services.Configure<IdentityServiceOptions>(
    builder.Configuration.GetSection(IdentityServiceOptions.SectionName));
builder.Services.Configure<AdminAuthenticationOptions>(
    builder.Configuration.GetSection(AdminAuthenticationOptions.SectionName));
builder.Services.Configure<GatewayProviderOptions>(
    builder.Configuration.GetSection(GatewayProviderOptions.SectionName));
builder.Services.Configure<OidcProviderOptions>(
    builder.Configuration.GetSection(OidcProviderOptions.SectionName));

// Login and JWKS need not share a host, so the provider may override the base address.
// Providers that resolve an absolute token endpoint ignore it.
builder.Services.AddHttpClient(
    AdminAuthenticationHttpClient.Name,
    (serviceProvider, client) =>
    {
        var providerOptions = serviceProvider
            .GetRequiredService<IOptions<GatewayProviderOptions>>().Value;
        var identity = serviceProvider
            .GetRequiredService<IOptions<IdentityServiceOptions>>().Value;
        var authority = string.IsNullOrWhiteSpace(providerOptions.Authority)
            ? identity.Authority
            : providerOptions.Authority;
        if (!string.IsNullOrWhiteSpace(authority))
        {
            client.BaseAddress = new Uri($"{authority.TrimEnd('/')}/");
        }

        client.Timeout = TimeSpan.FromSeconds(30);
    });

// Selection is resolved from options rather than from the configuration read above:
// values injected by a test host are not visible until builder.Build() runs, and a
// provider switch that cannot be exercised in tests is a provider switch nobody checks.
builder.Services.AddScoped<GatewayCredentialAuthenticator>();
builder.Services.AddScoped<OidcPasswordAuthenticator>();
builder.Services.AddScoped<IAdminCredentialAuthenticator>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<AdminAuthenticationOptions>>().Value;
    return options.Provider == AdminAuthenticationProvider.Oidc
        ? serviceProvider.GetRequiredService<OidcPasswordAuthenticator>()
        : serviceProvider.GetRequiredService<GatewayCredentialAuthenticator>();
});

builder.Services.AddScoped<AdminAccessTokenValidator>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
    .Configure<IOptions<IdentityServiceOptions>, IOptions<AdminAuthenticationOptions>, IHostEnvironment>(
        (options, identity, adminAuthentication, environment) =>
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
                OnMessageReceived = context =>
                {
                    var authorization = context.Request.Headers.Authorization.ToString();
                    if (!authorization.StartsWith(
                            "Bearer ",
                            StringComparison.OrdinalIgnoreCase) &&
                        context.Request.Cookies.TryGetValue(
                            adminAuthentication.Value.CookieName,
                            out var cookieToken))
                    {
                        context.Token = cookieToken;
                    }

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
builder.Services.AddSingleton<IAuthorizationHandler, AdminRoleHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("VocabularyAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AdminRoleRequirement());
    });
});

builder.Services.AddLexarborForwardedHeaders(builder.Configuration);
builder.Services.AddLexarborRateLimiting(builder.Configuration);

var app = builder.Build();

app.Logger.LogInformation("Lexarbor starting, version {Version}", ApplicationVersion.Current);
app.Logger.LogInformation("Listening: http://+:{Port}", httpPort);
if (persistentConfiguration != null)
{
    app.Logger.LogInformation(
        persistentConfiguration.Created
            ? "Created persistent configuration from image defaults at {ConfigurationPath}"
            : "Loaded persistent configuration from {ConfigurationPath}",
        persistentConfiguration.Path);
}
var sqliteConnection = new SqliteConnectionStringBuilder(connectionString);
app.Logger.LogInformation("Database: SQLite {DatabasePath}", sqliteConnection.DataSource);

var rateLimitOptions = app.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value;
var networkOptions = app.Services.GetRequiredService<IOptions<NetworkOptions>>().Value;
LogRateLimit("admin login", rateLimitOptions.AdminLogin);
LogRateLimit("public API", rateLimitOptions.PublicApi);
if (networkOptions.IsConfigured)
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
    using var credentialScope = app.Services.CreateScope();
    var authenticator = credentialScope.ServiceProvider
        .GetRequiredService<IAdminCredentialAuthenticator>();
    if (!authenticator.IsConfigured)
    {
        app.Logger.LogError(
            "Administrator login is not configured because the {Provider} provider is missing credentials. The service will continue running.",
            credentialScope.ServiceProvider
                .GetRequiredService<IOptions<AdminAuthenticationOptions>>().Value.Provider);
    }
}

// Configure database initialization.
if (builder.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
}

// Configure the HTTP request pipeline.
if (networkOptions.IsConfigured)
{
    // First, so that everything downstream — the rate limiter above all — sees
    // the client's address rather than the proxy's.
    //
    // Added only when a hop is trusted. ForwardedHeadersMiddleware skips its
    // origin check entirely when both KnownProxies and KnownIPNetworks are empty
    // and then applies X-Forwarded-For from anyone, so an always-on middleware
    // with empty trust lists trusts every caller rather than none — which would
    // let a caller choose its own rate limit partition.
    app.UseForwardedHeaders();
}

app.UseMiddleware<VocabularyExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
// Ahead of authentication so a rejected caller costs a partition lookup rather
// than a JWT validation, which for a cookie-bearing request can reach out to the
// identity provider for signing keys.
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CookieCsrfMiddleware>();
app.UseAuthorization();
app.MapAdminAuthEndpoints();
app.MapVocabularyHttpEndpoints(RateLimitingExtensions.PublicApiPolicy);
// Liveness only, and deliberately nothing more. The endpoint is anonymous
// because the container HEALTHCHECK has no credentials to present, so every
// field here is a field any caller can read; the build version used to be one
// of them, which told an unauthenticated caller which published release — and
// therefore which set of known issues — it was talking to. The version is now
// logged once at startup instead, where reading it requires access to the
// container's logs.
app.MapGet(
        "/health",
        () => VocabularyHttpResponse.Ok(new { status = "healthy" }))
    .AllowAnonymous();

string[] allHttpMethods =
[
    HttpMethods.Get,
    HttpMethods.Post,
    HttpMethods.Put,
    HttpMethods.Patch,
    HttpMethods.Delete,
    HttpMethods.Options
];
app.MapMethods(
        "/api/{**path}",
        allHttpMethods,
        () => VocabularyHttpResponse.NotFound("API endpoint was not found."))
    .RequireRateLimiting(RateLimitingExtensions.PublicApiPolicy);
app.MapMethods(
        "/admin/{**path}",
        allHttpMethods,
        () => VocabularyHttpResponse.NotFound("Admin endpoint was not found."))
    .RequireAuthorization("VocabularyAdmin");
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

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

static string BuildSqliteConnectionString(
    string? configuredConnectionString,
    string contentRootPath)
{
    var builder = new SqliteConnectionStringBuilder(
        string.IsNullOrWhiteSpace(configuredConnectionString)
            ? "Data Source=data/vocabulary.db"
            : configuredConnectionString);
    if (string.IsNullOrWhiteSpace(builder.DataSource))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Default must define a SQLite data source.");
    }

    if (!string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) &&
        !Path.IsPathRooted(builder.DataSource))
    {
        builder.DataSource = Path.GetFullPath(
            Path.Combine(contentRootPath, builder.DataSource));
    }

    // Microsoft.Data.Sqlite implements its timeout as a retry loop around
    // SQLITE_BUSY, so the library default meant a contended write held its
    // request thread for a full thirty seconds before failing -- longer than the
    // admin UI's own thirty-second HTTP timeout, so the caller saw a network
    // error rather than an answer. Writes are now serialized in process and
    // readers no longer block them under WAL, which leaves only brief
    // contention; five seconds rides out a checkpoint and still returns the 503
    // while the caller is waiting for it. An operator who needs a different
    // value sets `Default Timeout=` in the connection string, and any value
    // other than the library default is left alone.
    const int LibraryDefaultTimeoutSeconds = 30;
    if (builder.DefaultTimeout == LibraryDefaultTimeoutSeconds)
    {
        builder.DefaultTimeout = 5;
    }

    return builder.ToString();
}

public partial class Program
{
}
