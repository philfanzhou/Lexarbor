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
    private readonly string _databaseName;

    public VocabularyWebApplicationFactory()
        : this("Testing", includeAppCredentials: true)
    {
    }

    internal VocabularyWebApplicationFactory(
        string environment,
        bool includeAppCredentials)
    {
        _environment = environment;
        _includeAppCredentials = includeAppCredentials;
        _databaseName = $"vocabulary-http-{Guid.NewGuid():N}";
        Identity = new FakeIdentityState();
        Identity.AccessToken = CreateToken("admin");
    }

    public FakeIdentityState Identity { get; }

    public string CreateToken(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "identity-user"),
            new("preferred_username", "test-user")
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
                ["Database:InitializeOnStartup"] = "false",
                ["IdentityService:Authority"] = "http://identity.test",
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = Audience,
                ["IdentityService:AppId"] = _includeAppCredentials ? "vocabulary-app" : string.Empty,
                ["IdentityService:AppSecret"] = _includeAppCredentials ? "vocabulary-secret" : string.Empty,
                ["AdminAuthentication:CookieName"] = CookieName,
                ["AdminAuthentication:CookieSecure"] = "false"
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
                    options.Authority = null;
                    options.MetadataAddress = null!;
                    options.ConfigurationManager = null!;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Issuer,
                        ValidateAudience = true,
                        ValidAudience = Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(SigningSecret)),
                        ClockSkew = TimeSpan.FromSeconds(30),
                        RoleClaimType = "role",
                        NameClaimType = "preferred_username"
                    };
                });
        });
    }
}
