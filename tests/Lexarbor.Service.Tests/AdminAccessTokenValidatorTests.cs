using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lexarbor.Host.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

public class AdminAccessTokenValidatorTests
{
    [Fact]
    public async Task ValidateAsync_SigningKeyNotFound_RefreshesConfigurationAndRetries()
    {
        const string issuer = "test-issuer";
        const string audience = "test-audience";
        var signingKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("vocabulary-key-rollover-test-key-2026"));
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("role", "admin")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                signingKey,
                SecurityAlgorithms.HmacSha256)));
        var configurationManager = new RolloverConfigurationManager(signingKey);
        var options = new JwtBearerOptions
        {
            ConfigurationManager = configurationManager,
            RefreshOnIssuerKeyNotFound = true,
            MapInboundClaims = false,
            TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RoleClaimType = "role"
            }
        };
        var validator = new AdminAccessTokenValidator(
            new StaticOptionsMonitor<JwtBearerOptions>(options),
            NullLogger<AdminAccessTokenValidator>.Instance);

        var principal = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.True(principal.IsInRole("admin"));
        Assert.Equal(1, configurationManager.RefreshCount);
    }

    private sealed class RolloverConfigurationManager :
        IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly SecurityKey _signingKey;
        private bool _refreshed;

        public RolloverConfigurationManager(SecurityKey signingKey)
        {
            _signingKey = signingKey;
        }

        public int RefreshCount { get; private set; }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(
            CancellationToken cancel)
        {
            var configuration = new OpenIdConnectConfiguration();
            if (_refreshed)
            {
                configuration.SigningKeys.Add(_signingKey);
            }

            return Task.FromResult(configuration);
        }

        public void RequestRefresh()
        {
            RefreshCount++;
            _refreshed = true;
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public StaticOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
