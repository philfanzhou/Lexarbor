using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Authentication.Providers;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;

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

var identityOptions = builder.Configuration
    .GetSection(IdentityServiceOptions.SectionName)
    .Get<IdentityServiceOptions>() ?? new IdentityServiceOptions();
var adminAuthenticationOptions = builder.Configuration
    .GetSection(AdminAuthenticationOptions.SectionName)
    .Get<AdminAuthenticationOptions>() ?? new AdminAuthenticationOptions();

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
    .AddJwtBearer(options =>
    {
        options.Authority = identityOptions.Authority;
        options.Audience = identityOptions.Audience;
        options.RequireHttpsMetadata = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = identityOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = identityOptions.Audience,
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
                        adminAuthenticationOptions.CookieName,
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

app.Logger.LogInformation("Lexarbor starting");
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
            adminAuthenticationOptions.Provider);
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
app.MapGet(
        "/health",
        () => VocabularyHttpResponse.Ok(
            new { status = "healthy", version = ApplicationVersion.Current }))
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

    return builder.ToString();
}

public partial class Program
{
}
