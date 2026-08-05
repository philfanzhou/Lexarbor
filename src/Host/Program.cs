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
using Ruoyu.Study.Vocabulary.Database;
using Ruoyu.Study.Vocabulary.Database.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Ruoyu.Study.Vocabulary.Host.Authentication;
using Ruoyu.Study.Vocabulary.Host.Authentication.Providers;
using Ruoyu.Study.Vocabulary.Service;

var builder = WebApplication.CreateBuilder(args);

// HTTP listen port is hardcoded to 5008 (not configurable via ASPNETCORE_URLS).
// host port mapping is controlled by start.sh: -p ${Port}:5008.
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
builder.Services.Configure<QuantumZhouProviderOptions>(
    builder.Configuration.GetSection(QuantumZhouProviderOptions.SectionName));
builder.Services.Configure<OidcProviderOptions>(
    builder.Configuration.GetSection(OidcProviderOptions.SectionName));

// Login and JWKS need not share a host, so the provider may override the base address.
// Providers that resolve an absolute token endpoint ignore it.
builder.Services.AddHttpClient(
    AdminAuthenticationHttpClient.Name,
    (serviceProvider, client) =>
    {
        var providerOptions = serviceProvider
            .GetRequiredService<IOptions<QuantumZhouProviderOptions>>().Value;
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
builder.Services.AddScoped<QuantumZhouIdentityAuthenticator>();
builder.Services.AddScoped<OidcPasswordAuthenticator>();
builder.Services.AddScoped<IAdminCredentialAuthenticator>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<AdminAuthenticationOptions>>().Value;
    return options.Provider == AdminAuthenticationProvider.Oidc
        ? serviceProvider.GetRequiredService<OidcPasswordAuthenticator>()
        : serviceProvider.GetRequiredService<QuantumZhouIdentityAuthenticator>();
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

var app = builder.Build();

app.Logger.LogInformation("Vocabulary Service starting");
app.Logger.LogInformation("Listening: http://+:{Port}", httpPort);
var sqliteConnection = new SqliteConnectionStringBuilder(connectionString);
app.Logger.LogInformation("Database: SQLite {DatabasePath}", sqliteConnection.DataSource);
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
app.UseMiddleware<VocabularyExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<CookieCsrfMiddleware>();
app.UseAuthorization();
app.MapAdminAuthEndpoints();
app.MapVocabularyHttpEndpoints();
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
    () => VocabularyHttpResponse.NotFound("API endpoint was not found."));
app.MapMethods(
        "/admin/{**path}",
        allHttpMethods,
        () => VocabularyHttpResponse.NotFound("Admin endpoint was not found."))
    .RequireAuthorization("VocabularyAdmin");
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

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
