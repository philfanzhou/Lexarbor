using System.Data.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Consul.Shared;
using Ruoyu.Study.Vocabulary.Database;
using Ruoyu.Study.Vocabulary.Database.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Ruoyu.Study.Vocabulary.Host.Authentication;
using Ruoyu.Study.Vocabulary.Service;

var builder = WebApplication.CreateBuilder(args);

// ========== Consul Configuration Source ==========
builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);
var consulOptions = RuoyuConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = RuoyuConsulRuntimeState.Instance;

// ========== Serilog (Console + Grafana Loki) ==========
builder.Configuration.AddRuoyuLokiSink();
builder.Host.UseRuoyuSerilog("Ruoyu.Study.Vocabulary");

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
var connectionString = SharedPostgreSqlConnectionStringFactory.BuildOrFallback(
    builder.Configuration,
    builder.Configuration.GetConnectionString("Default"));
builder.Services.AddDbContext<VocabularyDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<IVocabularyRepository, VocabularyRepository>();
builder.Services.AddScoped<IVocabularyBookRepository, VocabularyBookRepository>();
builder.Services.AddScoped<IVocabularyMeaningRepository, VocabularyMeaningRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<VocabularyDomainService>();
builder.Services.AddScoped<VocabularyBookDomainService>();

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
builder.Services.AddHttpClient(
    IdentityTokenClient.HttpClientName,
    client =>
    {
        client.BaseAddress = new Uri($"{identityOptions.Authority.TrimEnd('/')}/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
builder.Services.AddScoped<IIdentityTokenClient, IdentityTokenClient>();

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
            RoleClaimType = "role",
            NameClaimType = "preferred_username"
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
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("VocabularyAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("admin");
    });
});

var app = builder.Build();

app.Logger.LogInformation("Vocabulary Service starting");
app.Logger.LogInformation(
    "Consul startup diagnostics: Address={Address}, Token={Token}, Source={Source}, KeyCount={KeyCount}, Prefixes={Prefixes}, LastError={LastError}",
    $"{consulOptions.Host}:{consulOptions.Port}",
    StartupDiagnosticsFormatter.MaskSecret(consulOptions.Token),
    consulRuntimeState.Source,
    consulRuntimeState.KeyCount,
    StartupDiagnosticsFormatter.SummarizePrefixes(consulRuntimeState.LoadedPrefixes),
    StartupDiagnosticsFormatter.SummarizeError(consulRuntimeState.LastError));
app.Logger.LogInformation("Listening: http://+:{Port}", httpPort);
if (!string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
app.Logger.LogInformation(
    "Effective configuration diagnostics: PostgreSqlHost={PostgreSqlHost}, PostgreSqlPort={PostgreSqlPort}, PostgreSqlUsername={PostgreSqlUsername}, PostgreSqlPassword={PostgreSqlPassword}, DatabaseName={DatabaseName}",
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Host"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Port"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Username"]),
    StartupDiagnosticsFormatter.SummarizePassword(builder.Configuration["PostgreSql:Password"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["Database:Name"]));
if (!app.Environment.IsDevelopment() &&
    !app.Environment.IsEnvironment("Testing") &&
    (string.IsNullOrWhiteSpace(identityOptions.AppId) ||
     string.IsNullOrWhiteSpace(identityOptions.AppSecret)))
{
    app.Logger.LogError(
        "Administrator login is not configured because Identity service application credentials are missing. The service will continue running.");
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

public partial class Program
{
}
