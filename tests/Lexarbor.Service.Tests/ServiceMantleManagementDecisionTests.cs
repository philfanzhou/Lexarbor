using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ServiceMantle.Audit;
using ServiceMantle.Management;

namespace Lexarbor.Service.Tests;

/// <summary>
/// A consumer-only prototype for issue 145. It is never registered by Program.
/// The request adapter owns credentials; the ServiceMantle provider SPI sees only
/// a scoped accessor, and identity is derived solely from Lexarbor's validated JWT.
/// Password proxy login is gone, so the prototype consumes the same Bearer token
/// the HTTP surface would.
/// </summary>
public class ServiceMantleManagementDecisionTests
{
    [Fact]
    public async Task ValidAdmin_MapsValidatedJwtToManagementPermission()
    {
        using var factory = new VocabularyWebApplicationFactory();

        var result = await InvokePrototypeAsync(factory, factory.CreateToken("admin"));

        Assert.Equal(ManagementIdentityStatus.Authenticated, result.Status);
        Assert.Equal(ManagementPermission.Admin, Assert.Single(result.Identity!.Permissions));
        Assert.NotEmpty(result.Identity.OperatorId);
        Assert.Equal(WellKnownManagementAuditOperatorSources.InteractiveAdmin, result.Identity.Source);
    }

    [Theory]
    [InlineData("regular-user", ManagementIdentityStatus.Unauthenticated)]
    [InlineData("missing-credentials", ManagementIdentityStatus.Unauthenticated)]
    [InlineData("wrong-signature", ManagementIdentityStatus.Failed)]
    [InlineData("expired", ManagementIdentityStatus.Failed)]
    [InlineData("malformed", ManagementIdentityStatus.Failed)]
    public async Task RejectedOrUnavailableIdentity_NeverProducesManagementPermission(
        string scenario,
        ManagementIdentityStatus expected)
    {
        using var factory = new VocabularyWebApplicationFactory();
        string? token = scenario switch
        {
            "regular-user" => factory.CreateToken("student"),
            "missing-credentials" => null,
            "wrong-signature" => CreateToken(
                "different-signing-key-for-prototype-tests",
                DateTime.UtcNow.AddMinutes(30)),
            "expired" => CreateToken(
                VocabularyWebApplicationFactory.SigningSecret,
                DateTime.UtcNow.AddMinutes(-5)),
            "malformed" => "not-a-jwt",
            _ => null
        };

        var result = await InvokePrototypeAsync(factory, token);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Identity);
    }

    private static async Task<ManagementIdentityResult> InvokePrototypeAsync(
        VocabularyWebApplicationFactory factory,
        string? accessToken)
    {
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext();
        if (accessToken is not null)
        {
            context.Request.Headers.Authorization = "Bearer " + accessToken;
        }
        return await PrototypeBearerAdapterAsync(
            context,
            scope.ServiceProvider,
            TestContext.Current.CancellationToken);
    }

    private static async ValueTask<ManagementIdentityResult> PrototypeBearerAdapterAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization["Bearer ".Length..]))
        {
            return ManagementIdentityResult.Unauthenticated();
        }

        var provider = new PrototypeIdentityProvider(
            authorization["Bearer ".Length..],
            services.GetRequiredService<AdminAccessTokenValidator>(),
            services.GetRequiredService<IOptions<AdminAuthenticationOptions>>());
        return await ManagementIdentityProviderInvoker.InvokeAsync(provider, cancellationToken);
    }

    private sealed class PrototypeIdentityProvider(
        string accessToken,
        AdminAccessTokenValidator validator,
        IOptions<AdminAuthenticationOptions> options) : IManagementIdentityProvider
    {
        public async ValueTask<ManagementIdentityResult> GetIdentityAsync(
            CancellationToken cancellationToken = default)
        {
            var principal = await validator.ValidateAsync(accessToken, cancellationToken);
            if (principal is null)
            {
                return ManagementIdentityResult.Failed("identity.token_invalid");
            }

            if (!VocabularyClaims.HasRole(principal, options.Value.RequiredRole))
            {
                return ManagementIdentityResult.Unauthenticated();
            }

            var operatorId = principal.FindFirst("sub")?.Value ??
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(operatorId))
            {
                return ManagementIdentityResult.Failed("identity.subject_missing");
            }

            return ManagementIdentityResult.Authenticated(ManagementIdentity.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                operatorId,
                [ManagementPermission.Admin],
                VocabularyClaims.GetDisplayName(principal)));
        }
    }

    private static string CreateToken(string signingSecret, DateTime expires)
    {
        var token = new JwtSecurityToken(
            issuer: VocabularyWebApplicationFactory.Issuer,
            audience: VocabularyWebApplicationFactory.Audience,
            claims:
            [
                new Claim("sub", "prototype-admin"),
                new Claim("name", "Prototype Admin"),
                new Claim("role", "admin")
            ],
            notBefore: expires.AddMinutes(-30),
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingSecret)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
