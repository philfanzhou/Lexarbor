using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Vocabulary.Database;

namespace Ruoyu.Study.Vocabulary.Service.Tests.TestInfrastructure;

public sealed class VocabularyWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "QuantumZhou.Identity";
    public const string Audience = "QuantumZhou.microservices";
    public const string CookieName = "ruoyuVocabularyAdmin";
    public const string SigningSecret = "vocabulary-test-signing-key-2026-07-29";

    private readonly string _environment;
    private readonly bool _includeAppCredentials;
    private readonly string _provider;
    private readonly string _databaseName;

    public VocabularyWebApplicationFactory()
        : this("Testing", includeAppCredentials: true)
    {
    }

    internal VocabularyWebApplicationFactory(
        string environment,
        bool includeAppCredentials,
        string provider = "QuantumZhou")
    {
        _environment = environment;
        _includeAppCredentials = includeAppCredentials;
        _provider = provider;
        _databaseName = $"vocabulary-http-{Guid.NewGuid():N}";
        Identity = new FakeIdentityState();
        Identity.AccessToken = CreateToken("admin");
    }

    public FakeIdentityState Identity { get; }

    /// <summary>
    /// Mints a token in the exact shape QuantumZhou.Identity produces: it builds its
    /// JwtPayload directly, bypassing the outbound claim type map, so claims keep their
    /// full ClaimTypes URIs instead of the short JWT names. Emitting short names here
    /// would make these tests assert a contract the real issuer does not implement.
    /// </summary>
    public string CreateToken(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "identity-user"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test-user")
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

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
                ["Database:InitializeOnStartup"] = "false",
                ["IdentityService:Authority"] = "http://identity.test",
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = Audience,
                ["AdminAuthentication:CookieName"] = CookieName,
                ["AdminAuthentication:CookieSecure"] = "false",
                ["AdminAuthentication:Provider"] = _provider,
                ["AdminAuthentication:QuantumZhou:AppId"] =
                    _includeAppCredentials ? "vocabulary-app" : string.Empty,
                ["AdminAuthentication:QuantumZhou:AppSecret"] =
                    _includeAppCredentials ? "vocabulary-secret" : string.Empty,
                ["AdminAuthentication:Oidc:TokenEndpoint"] =
                    "http://identity.test/protocol/openid-connect/token",
                ["AdminAuthentication:Oidc:ClientId"] =
                    _includeAppCredentials ? "vocabulary-client" : string.Empty,
                ["AdminAuthentication:Oidc:ClientSecret"] =
                    _includeAppCredentials ? "vocabulary-client-secret" : string.Empty
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<VocabularyDbContext>>();
            services.RemoveAll<VocabularyDbContext>();
            services.AddDbContext<VocabularyDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.AddSingleton(Identity);
            services.AddTransient<FakeIdentityHandler>();
            services.AddHttpClient("VocabularyIdentity")
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
}
