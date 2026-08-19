using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Lexarbor.Database;

namespace Lexarbor.Service.Tests.TestInfrastructure;

public sealed class VocabularyWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "http://localhost:8080";
    public const string Audience = "lexarbor";
    public const string CookieName = "lexarborAdmin";
    public const string SigningSecret = "vocabulary-test-signing-key-2026-07-29";

    /// <summary>
    /// Request header the test host reads to set the connection's remote address.
    /// TestServer leaves that address null, which would put every request in the
    /// rate limiter's single "unknown" partition and make a per-address limit
    /// impossible to tell apart from a global one.
    /// </summary>
    public const string ClientAddressHeader = "X-Test-Client-Address";

    private readonly string _environment;
    private readonly bool _includeAppCredentials;
    private readonly string _provider;
    private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;
    private readonly SqliteConnection _databaseConnection;

    public VocabularyWebApplicationFactory()
        : this("Testing", includeAppCredentials: true)
    {
    }

    internal VocabularyWebApplicationFactory(
        string environment,
        bool includeAppCredentials,
        string provider = "Gateway",
        IReadOnlyDictionary<string, string?>? extraConfiguration = null)
    {
        _environment = environment;
        _includeAppCredentials = includeAppCredentials;
        _provider = provider;
        _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
        _databaseConnection = new SqliteConnection("Data Source=:memory:");
        _databaseConnection.Open();
        Identity = new FakeIdentityState();
        Identity.AccessToken = CreateToken("admin");
    }

    public FakeIdentityState Identity { get; }

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

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:InitializeOnStartup"] = "true",
                ["IdentityService:Authority"] = "http://identity.test",
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = Audience,
                ["AdminAuthentication:CookieName"] = CookieName,
                ["AdminAuthentication:CookieSecure"] = "false",
                ["AdminAuthentication:Provider"] = _provider,
                ["AdminAuthentication:Gateway:AppId"] =
                    _includeAppCredentials ? "vocabulary-app" : string.Empty,
                ["AdminAuthentication:Gateway:AppSecret"] =
                    _includeAppCredentials ? "vocabulary-secret" : string.Empty,
                ["AdminAuthentication:Oidc:TokenEndpoint"] =
                    "http://identity.test/protocol/openid-connect/token",
                ["AdminAuthentication:Oidc:ClientId"] =
                    _includeAppCredentials ? "vocabulary-client" : string.Empty,
                ["AdminAuthentication:Oidc:ClientSecret"] =
                    _includeAppCredentials ? "vocabulary-client-secret" : string.Empty
            });

            // Last, so a test can override any default above.
            configuration.AddInMemoryCollection(_extraConfiguration);
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>();
            services.RemoveAll<DbContextOptions<VocabularyDbContext>>();
            services.RemoveAll<VocabularyDbContext>();
            services.AddDbContext<VocabularyDbContext>(options =>
                options.UseSqlite(_databaseConnection));

            services.AddSingleton(Identity);
            services.AddTransient<FakeIdentityHandler>();
            services.AddHttpClient("LexarborIdentity")
                .ConfigurePrimaryHttpMessageHandler<FakeIdentityHandler>();

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
            _databaseConnection.Dispose();
        }
    }
}
