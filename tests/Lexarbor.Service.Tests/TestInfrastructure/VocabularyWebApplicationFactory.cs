using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Lexarbor.Database;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests.TestInfrastructure;

public sealed class VocabularyWebApplicationFactory : WebApplicationFactory<Program>
{
    // The fake identity host keeps its pre-migration loopback address: the loopback
    // http issuer plus an https redirect is the HTTPS-profile regression baseline, and
    // the official client (0.1.16) accepts http and https authorities alike, so the
    // literal address is a topology choice, not a requirement.
    public const string Issuer = "http://127.0.0.1:8080";
    public const string Audience = "lexarbor";
    /// <summary>
    /// The retired password-login JWT cookie name. The cookie is no longer
    /// authenticated; the constant remains for tests that prove exactly that and
    /// for the logout cleanup assertions.
    /// </summary>
    public const string CookieName = "lexarborAdmin";
    public const string SigningSecret = "vocabulary-test-signing-key-2026-07-29";

    /// <summary>
    /// Request header the test host reads to set the connection's remote address.
    /// TestServer leaves that address null, which would put every request in the
    /// rate limiter's single "unknown" partition and make a per-address limit
    /// impossible to tell apart from a global one.
    /// </summary>
    public const string ClientAddressHeader = "X-Test-Client-Address";

    private readonly string _keyContentRoot;
    private readonly bool _ownsKeyContentRoot;
    private readonly string _environment;
    private readonly bool _useProductionDatabase;
    private readonly string? _contentRootPath;
    private readonly bool _includeAppCredentials;
    private readonly string _provider;
    private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;
    private static readonly object NetworkConfigurationLock = new();

    public VocabularyWebApplicationFactory()
        : this("Testing", includeAppCredentials: true)
    {
    }

    internal VocabularyWebApplicationFactory(
        string environment,
        bool includeAppCredentials,
        string provider = "OidcCode",
        IReadOnlyDictionary<string, string?>? extraConfiguration = null,
        string? keyContentRoot = null,
        string? databasePath = null,
        bool useProductionDatabase = false,
        string? contentRootPath = null)
    {
        _ownsKeyContentRoot = keyContentRoot is null;
        _keyContentRoot = keyContentRoot ?? Path.Combine(
            Path.GetTempPath(), $"lexarbor-host-keys-{Guid.NewGuid():N}");
        _environment = environment;
        _useProductionDatabase = useProductionDatabase;
        _contentRootPath = contentRootPath;
        _includeAppCredentials = includeAppCredentials;
        _provider = provider;
        _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
        // The startup gate accepts real files only (:memory: is an invalid
        // target), so every host — the default included — works on its own
        // file under the gate-safe directory. Like production, the hosts get
        // the connection string rather than one shared open connection: with
        // Pooling=False every operation opens and closes its own connection,
        // so SQLite checkpoints the write-ahead log away between operations
        // and a second host (WithWebHostBuilder) starts against a file the
        // strict gate can observe — a held-open connection would keep the
        // -wal/-shm sidecars alive and look like an unrecovered crash. A test
        // may instead supply the whole connection string through
        // ConnectionStrings:Default, which is how non-default options such as
        // Default Timeout reach the gate unchanged.
        _databaseDirectory = databasePath is null
            ? CreateGateSafeDirectory($"lexarbor-host-{Guid.NewGuid():N}")
            : null;
        _databasePath = databasePath ?? Path.Combine(_databaseDirectory!, "vocabulary.db");
        _databaseConnectionString =
            extraConfiguration is not null
            && extraConfiguration.TryGetValue("ConnectionStrings:Default", out var configured)
            && !string.IsNullOrWhiteSpace(configured)
                ? configured
                : $"Data Source={_databasePath};Pooling=False";
    }

    private readonly string? _databaseDirectory;
    private readonly string _databasePath;
    private readonly string _databaseConnectionString;
    private string DatabaseConnectionString => _databaseConnectionString;

    /// <summary>
    /// A database directory whose every path segment from the root is a real
    /// directory: the strict startup gate rejects paths that resolve through
    /// symbolic links, which on macOS rules out <see cref="Path.GetTempPath"/>
    /// (it lives under /var). The test output directory inside the repository
    /// satisfies the rule on every platform this suite runs on.
    /// </summary>
    public static string CreateGateSafeDirectory(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "gate-databases", name);
        var resolved = Path.GetFullPath(directory);
        for (var current = resolved; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)!)
        {
            if (File.Exists(current))
            {
                break;
            }

            if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new InvalidOperationException(
                    $"The test database directory resolves through a symbolic link, which the " +
                    $"startup gate rejects: {Path.GetFileName(current)}");
            }
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal hosting reads configuration before ConfigureWebHost can add the
        // in-memory test overrides. Forwarded-header trust and the ServiceMantle
        // instance id are read at that point, so expose those values during host
        // construction only. The explicit production-database mode also passes
        // the connection setting before Program captures it, retaining its real
        // DbContext registration. Serialize factory startup to keep process
        // environment changes isolated.
        lock (NetworkConfigurationLock)
        {
            // Telemetry keys ride the same locked environment-variable
            // passthrough: the Host reads them before builder.Build(), which
            // happens inside this lock, so no other concurrently starting test
            // host can observe them.
            var networkSettings = _extraConfiguration
                .Where(entry => entry.Key.StartsWith("Network:", StringComparison.Ordinal)
                    || entry.Key.StartsWith("Service:", StringComparison.Ordinal)
                    || entry.Key.StartsWith("Telemetry:", StringComparison.Ordinal)
                    || (_useProductionDatabase && entry.Key.StartsWith("ConnectionStrings:", StringComparison.Ordinal)))
                .Select(entry => (Name: entry.Key.Replace(":", "__", StringComparison.Ordinal), entry.Value))
                .ToArray();
            var previous = networkSettings
                .Select(entry => (entry.Name, Value: Environment.GetEnvironmentVariable(entry.Name)))
                .ToArray();
            try
            {
                foreach (var entry in networkSettings)
                {
                    Environment.SetEnvironmentVariable(entry.Name, entry.Value);
                }

                var host = base.CreateHost(builder);
                // The antiforgery boundary issues and validates its Secure cookie
                // pairs only over an https request: the in-memory test server presents
                // an https origin — exactly what a real browser deployment does. A
                // test that swaps in a real Kestrel binding is unaffected.
                if (host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                    is Microsoft.AspNetCore.TestHost.TestServer testServer)
                {
                    testServer.BaseAddress = new Uri("https://localhost");
                }
                return host;
            }
            finally
            {
                foreach (var entry in previous)
                {
                    Environment.SetEnvironmentVariable(entry.Name, entry.Value);
                }
            }
        }
    }

    /// <summary>
    /// Mints a token with the standard short OIDC claim names "sub", "name" and "role".
    /// IdentityClaimShapeTests separately pins the full ClaimTypes URI shape.
    /// </summary>
    public string CreateToken(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "identity-user"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("name", "test-user")
        };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        return MintToken(Audience, claims);
    }

    /// <summary>
    /// Mints a token for an arbitrary audience and claim set, for tests that model a
    /// specific issuer's token rather than the generic one above.
    /// </summary>
    public static string MintToken(string audience, IEnumerable<Claim> claims)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Mints a valid opaque administrator session cookie. The legacy JWT cookie is no
    /// longer authenticated, so tests that exercise cookie-based administration use a
    /// real session instead. Blocking is safe: xUnit supplies no synchronization
    /// context.
    /// </summary>
    public string CreateSessionCookie(string role = "admin") =>
        CreateSessionCookieAsync(role).GetAwaiter().GetResult();

    private async Task<string> CreateSessionCookieAsync(string role)
    {
        using var scope = Services.CreateScope();
        var handle = await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new ValidatedAdminSession
        {
            AccessToken = "synthetic-access-marker",
            Issuer = Issuer,
            Subject = "session-subject",
            DisplayName = "session-user",
            Roles = [role],
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        }, TestContext.Current.CancellationToken);
        return $"{AdminSessionCookie.Name}={handle}";
    }

    /// <summary>
    /// The hosted-login antiforgery boundary issues and validates its Secure cookie
    /// pairs only over an https request, so the factory's default clients present an
    /// https origin — exactly what a real browser deployment does. Clients created
    /// through a host derived with WithWebHostBuilder are typed as the base factory:
    /// those call sites use <see cref="CreateHttpsClient"/>.
    /// </summary>
    public new HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

    public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        // The options' default base address is the plain-http localhost origin; the
        // in-memory test server presents https instead (see the summary above). A
        // real Kestrel binding from UseKestrel is left exactly as it is.
        if (options.BaseAddress is { } defaultBase
            && defaultBase.Host == "localhost"
            && defaultBase.Port == 80
            && IsInMemoryTestServer())
        {
            options.BaseAddress = new Uri("https://localhost");
        }
        return base.CreateClient(options);
    }

    private bool IsInMemoryTestServer()
    {
        try
        {
            return Server is Microsoft.AspNetCore.TestHost.TestServer;
        }
        catch (NotSupportedException)
        {
            // UseKestrel hosts do not expose the Server property.
            return false;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        if (_contentRootPath is not null) builder.UseContentRoot(_contentRootPath);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:InitializeOnStartup"] = "true",
                // Each test factory instance owns its root-key file, isolating
                // the ServiceMantle key repository per test.
                ["DataProtection:RootKeyFile"] = Path.Combine(_keyContentRoot, "root-key"),
                ["IdentityService:Authority"] = "http://127.0.0.1:8080",
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = Audience,
                // The fake identity host is an http one, and several tests run
                // under Production, where HTTPS metadata is required. Set here
                // rather than left to the environment default so that a test
                // exercising the requirement can turn it back on.
                ["IdentityService:RequireHttpsMetadata"] = "false",
                ["AdminAuthentication:Provider"] = _provider,
                ["AdminAuthentication:OidcCode:ClientId"] =
                    _includeAppCredentials ? "vocabulary-client" : string.Empty,
                ["AdminAuthentication:OidcCode:ClientSecret"] =
                    _includeAppCredentials ? "vocabulary-client-secret" : string.Empty
            });

            // Last, so a test can override any default above.
            configuration.AddInMemoryCollection(_extraConfiguration);
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>();
            if (!_useProductionDatabase)
            {
                services.RemoveAll<DbContextOptions<VocabularyDbContext>>();
                services.RemoveAll<IDbContextFactory<VocabularyDbContext>>();
                services.RemoveAll<VocabularyDbContext>();
                services.AddDbContextFactory<VocabularyDbContext>(options =>
                    options.UseSqlite(DatabaseConnectionString));
                services.AddScoped(serviceProvider =>
                    serviceProvider.GetRequiredService<IDbContextFactory<VocabularyDbContext>>().CreateDbContext());
            }

            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    // Swap the signing key and cut the network metadata lookup, but keep
                    // the validation parameters Program.cs built. Replacing them wholesale
                    // let the double drift away from production claim types, which is how
                    // the role-claim mismatch stayed green.
                    options.Authority = null;
                    options.MetadataAddress = null!;
                    options.ConfigurationManager = null!;
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningSecret));
                });
        });
    }

    /// <summary>
    /// Sets <see cref="ConnectionInfo.RemoteIpAddress"/> from a request header,
    /// ahead of everything the application registers. It writes the same property
    /// Kestrel would, so the code under test reads the address the same way in
    /// both hosts, and it does nothing when the header is absent.
    /// </summary>
    private sealed class ClientAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(
                            ClientAddressHeader,
                            out var address) &&
                        IPAddress.TryParse(address.ToString(), out var parsed))
                    {
                        context.Connection.RemoteIpAddress = parsed;
                    }

                    await nextMiddleware();
                });
                next(builder);
            };
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            if (_ownsKeyContentRoot && Directory.Exists(_keyContentRoot))
            {
                Directory.Delete(_keyContentRoot, recursive: true);
            }

            if (_databaseDirectory is not null && Directory.Exists(_databaseDirectory))
            {
                try
                {
                    Directory.Delete(_databaseDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // A WAL sidecar that SQLite still holds is left behind with
                    // the directory; the next run never reuses this name.
                }
            }
        }
    }
}


/// <summary>
/// Creates a client with an https origin from a host whose static type is the base
/// factory (a WithWebHostBuilder derivative): the antiforgery boundary's Secure
/// cookie pairs are issued and validated only over https requests.
/// </summary>
public static class WebApplicationFactoryHttpsExtensions
{
    public static HttpClient CreateHttpsClient(this Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
}
