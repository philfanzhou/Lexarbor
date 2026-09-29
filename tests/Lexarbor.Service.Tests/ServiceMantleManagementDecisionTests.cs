using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
/// </summary>
public class ServiceMantleManagementDecisionTests
{
    [Fact]
    public async Task ValidAdmin_MapsValidatedJwtToManagementPermission()
    {
        using var factory = new VocabularyWebApplicationFactory();
        factory.Identity.AccessToken = factory.CreateToken("admin");

        var result = await InvokePrototypeAsync(factory);

        Assert.Equal(ManagementIdentityStatus.Authenticated, result.Status);
        Assert.Equal(ManagementPermission.Admin, Assert.Single(result.Identity!.Permissions));
        Assert.NotEmpty(result.Identity.OperatorId);
        Assert.Equal(WellKnownManagementAuditOperatorSources.InteractiveAdmin, result.Identity.Source);
    }

    [Theory]
    [InlineData("regular-user", ManagementIdentityStatus.Unauthenticated)]
    [InlineData("invalid-credentials", ManagementIdentityStatus.Unauthenticated)]
    [InlineData("wrong-signature", ManagementIdentityStatus.Failed)]
    [InlineData("expired", ManagementIdentityStatus.Failed)]
    [InlineData("identity-unavailable", ManagementIdentityStatus.Failed)]
    public async Task RejectedOrUnavailableIdentity_NeverProducesManagementPermission(
        string scenario,
        ManagementIdentityStatus expected)
    {
        using var factory = new VocabularyWebApplicationFactory();
        factory.Identity.AccessToken = factory.CreateToken("admin");
        switch (scenario)
        {
            case "regular-user":
                factory.Identity.AccessToken = factory.CreateToken("student");
                break;
            case "invalid-credentials":
                factory.Identity.Mode = FakeIdentityMode.InvalidCredentials;
                break;
            case "wrong-signature":
                factory.Identity.AccessToken = CreateToken(
                    "different-signing-key-for-prototype-tests",
                    DateTime.UtcNow.AddMinutes(30));
                break;
            case "expired":
                factory.Identity.AccessToken = CreateToken(
                    VocabularyWebApplicationFactory.SigningSecret,
                    DateTime.UtcNow.AddMinutes(-5));
                break;
            case "identity-unavailable":
                factory.Identity.Mode = FakeIdentityMode.Unavailable;
                break;
        }

        var result = await InvokePrototypeAsync(factory);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Identity);
    }

    private static async Task<ManagementIdentityResult> InvokePrototypeAsync(
        VocabularyWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes("{\"username\":\"admin\",\"password\":\"test-password\"}"));
        return await PrototypeLoginAdapterAsync(
            context,
            scope.ServiceProvider,
            TestContext.Current.CancellationToken);
    }

    private static async ValueTask<ManagementIdentityResult> PrototypeLoginAdapterAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var request = await JsonSerializer.DeserializeAsync<AdminAuthEndpoints.AdminLoginRequest>(
            context.Request.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);
        if (request is null || string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return ManagementIdentityResult.Unauthenticated();
        }

        var credentials = new PrototypeCredentials(request.Username, request.Password);
        var provider = new PrototypeIdentityProvider(
            credentials,
            services.GetRequiredService<IAdminCredentialAuthenticator>(),
            services.GetRequiredService<AdminAccessTokenValidator>(),
            services.GetRequiredService<IOptions<AdminAuthenticationOptions>>());
        return await ManagementIdentityProviderInvoker.InvokeAsync(provider, cancellationToken);
    }

    private sealed record PrototypeCredentials(string Username, string Password);

    private sealed class PrototypeIdentityProvider(
        PrototypeCredentials credentials,
        IAdminCredentialAuthenticator authenticator,
        AdminAccessTokenValidator validator,
        IOptions<AdminAuthenticationOptions> options) : IManagementIdentityProvider
    {
        public async ValueTask<ManagementIdentityResult> GetIdentityAsync(
            CancellationToken cancellationToken = default)
        {
            var exchange = await authenticator.AuthenticateAsync(
                credentials.Username,
                credentials.Password,
                cancellationToken);
            if (exchange.Status == AdminCredentialStatus.InvalidCredentials)
            {
                return ManagementIdentityResult.Unauthenticated();
            }

            if (exchange.Status != AdminCredentialStatus.Success ||
                string.IsNullOrWhiteSpace(exchange.AccessToken))
            {
                return ManagementIdentityResult.Failed("identity.unavailable");
            }

            var principal = await validator.ValidateAsync(exchange.AccessToken, cancellationToken);
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
