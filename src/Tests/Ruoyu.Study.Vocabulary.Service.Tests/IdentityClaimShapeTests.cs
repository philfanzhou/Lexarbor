using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Vocabulary.Host.Authentication;
using Ruoyu.Study.Vocabulary.Service.Tests.TestInfrastructure;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

/// <summary>
/// Pins the claim shapes this service must accept.
///
/// QuantumZhou.Identity emits the standard short names ("sub", "name", "role"). It
/// previously emitted the full <see cref="ClaimTypes"/> URIs, and tokens minted before
/// that switch stay valid until they expire, so both shapes have to keep working —
/// do not delete the long-URI cases here.
///
/// This service runs with <c>MapInboundClaims = false</c>, so nothing rewrites claim
/// names on the way in: whatever the issuer wrote is what the handlers see. That is why
/// every read goes through <see cref="VocabularyClaims"/> rather than
/// <c>User.Identity.Name</c> or <c>IsInRole</c>, which can only match one shape.
/// </summary>
public class IdentityClaimShapeTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private readonly VocabularyWebApplicationFactory _factory;

    public IdentityClaimShapeTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Identity.Reset();
    }

    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("role")]
    public async Task AdminEndpoint_AcceptsAdminRoleInEitherClaimShape(string roleClaimType)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(new Claim(roleClaimType, "admin")));

        var response = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("role")]
    public async Task AdminEndpoint_RejectsNonAdminInEitherClaimShape(string roleClaimType)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(new Claim(roleClaimType, "student")));

        var response = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The pairs mirror whole tokens rather than a cross-product: an issuer emits one
    /// shape or the other, never a mix.
    /// </summary>
    [Theory]
    [InlineData("role", "name")]
    [InlineData(ClaimTypes.Role, ClaimTypes.Name)]
    public async Task Session_ResolvesUsernameFromIdentityNameClaim(
        string roleClaimType,
        string nameClaimType)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(
                new Claim(roleClaimType, "admin"),
                new Claim(nameClaimType, "bootstrap-admin")));

        var response = await client.GetAsync("/admin/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("bootstrap-admin");
        body.Should().Contain("admin");
    }

    /// <summary>
    /// Identity omits the display name claim when the account has no display name. The
    /// session must still resolve, falling back to the subject identifier.
    /// </summary>
    [Theory]
    [InlineData("role", "sub")]
    [InlineData(ClaimTypes.Role, ClaimTypes.NameIdentifier)]
    public async Task Session_FallsBackToSubjectWhenNameClaimIsAbsent(
        string roleClaimType,
        string subjectClaimType)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(
                new Claim(roleClaimType, "admin"),
                new Claim(subjectClaimType, "account-42")));

        var response = await client.GetAsync("/admin/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("account-42");
    }

    /// <summary>
    /// The required role is read from options at evaluation time, so a deployment whose
    /// issuer names the role differently can point at it without a code change.
    /// </summary>
    [Fact]
    public async Task AdminPolicy_HonoursConfiguredRequiredRole()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdminAuthentication:RequiredRole"] = "vocabulary-curator"
                })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(new Claim(ClaimTypes.Role, "vocabulary-curator")));
        var curator = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(new Claim(ClaimTypes.Role, "admin")));
        var admin = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        curator.StatusCode.Should().Be(HttpStatusCode.OK);
        admin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void GetRoles_DeduplicatesAcrossBothClaimShapes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("role", "admin"),
            new Claim("role", "student")
        ]));

        VocabularyClaims.GetRoles(principal).Should().BeEquivalentTo("admin", "student");
    }

    private HttpClient CreateClient()
    {
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private static string CreateToken(params Claim[] claims)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(VocabularyWebApplicationFactory.SigningSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: VocabularyWebApplicationFactory.Issuer,
            audience: VocabularyWebApplicationFactory.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
