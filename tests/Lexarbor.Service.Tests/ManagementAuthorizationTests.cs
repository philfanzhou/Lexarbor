using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Lexarbor.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ServiceMantle.Web.Management;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The administrator authorization after the ServiceMantle management-identity
/// migration: both principal sources (the opaque session cookie and the Bearer
/// token) are mapped onto the ServiceMantle operator contract by the host, the
/// <c>ServiceMantle.ManagementAdmin</c> policy protects every <c>/admin/*</c>
/// route, and the observable contract is unchanged: anonymous 401,
/// authenticated non-administrator 403, administrator 200, the session response
/// built from the original claims, and client-injected
/// <c>servicemantle.*</c> claims never elevating anyone.
/// </summary>
public class ManagementAuthorizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertAdministratorReadAndWriteAsync(
        HttpClient client,
        string label)
    {
        foreach (var path in new[] { "/admin/auth/session", "/admin/system/version" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{label} GET {path}");
        }

        using var write = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = label, status = true },
            Ct);
        Assert.True(write.StatusCode == HttpStatusCode.OK, $"{label} POST /admin/vocabulary-books");
    }

    private static async Task AssertForbiddenEverywhereAsync(HttpClient client, string label)
    {
        using var read = await client.GetAsync("/admin/auth/session", Ct);
        Assert.True(read.StatusCode == HttpStatusCode.Forbidden, $"{label} GET");
        using var write = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = label, status = true },
            Ct);
        Assert.True(write.StatusCode == HttpStatusCode.Forbidden, $"{label} POST");
    }

    private static HttpClient CreateSessionClient(VocabularyWebApplicationFactory factory, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie(role));
        // Cookie-authenticated writes keep their CSRF marker requirement.
        AdminTestAntiforgery.Attach(client, factory);
        return client;
    }

    private static HttpClient CreateBearerClient(VocabularyWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string MintToken(params Claim[] claims) =>
        VocabularyWebApplicationFactory.MintToken(
            VocabularyWebApplicationFactory.Audience,
            claims);

    [Fact]
    public async Task SessionCookieAdministrator_ReadsAndWrites()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = CreateSessionClient(factory, "admin");

        await AssertAdministratorReadAndWriteAsync(client, "Session Admin Book");
    }

    [Fact]
    public async Task BearerAdministrator_ReadsAndWrites()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = CreateBearerClient(factory, factory.CreateToken("admin"));

        await AssertAdministratorReadAndWriteAsync(client, "Bearer Admin Book");
    }

    [Fact]
    public async Task SessionCookieRegularRole_IsForbiddenOnReadAndWrite()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = CreateSessionClient(factory, "student");

        await AssertForbiddenEverywhereAsync(client, "Session Student Book");
    }

    [Fact]
    public async Task BearerRegularRole_IsForbiddenOnReadAndWrite()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = CreateBearerClient(factory, factory.CreateToken("student"));

        await AssertForbiddenEverywhereAsync(client, "Bearer Student Book");
    }

    [Fact]
    public async Task Anonymous_Received401WithTheFixedMessage()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();

        using var read = await client.GetAsync("/admin/auth/session", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Authentication is required.", body.RootElement.GetProperty("message").GetString());

        using var write = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = "Anonymous Book", status = true },
            Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task ExpiredBearerToken_IsRejectedAsUnauthenticated()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var expired = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: VocabularyWebApplicationFactory.Issuer,
            audience: VocabularyWebApplicationFactory.Audience,
            claims:
            [
                new Claim("sub", "expired-admin"),
                new Claim("name", "Expired Admin"),
                new Claim("role", "admin")
            ],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(VocabularyWebApplicationFactory.SigningSecret)),
                SecurityAlgorithms.HmacSha256)));
        using var client = CreateBearerClient(factory, expired);

        using var response = await client.GetAsync("/admin/auth/session", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ForgedServiceMantleClaims_DoNotElevateARegularRole()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var forged = MintToken(
            new Claim("sub", "attacker"),
            new Claim("role", "student"),
            new Claim("servicemantle.operator_id", "attacker"),
            new Claim("servicemantle.operator_source", "interactive_admin"),
            new Claim("servicemantle.permission", "management.admin"));
        using var client = CreateBearerClient(factory, forged);

        // The mapping strips the forged identity before its own decision, and
        // the role check denies one, so the injected admin permission grants
        // nothing.
        await AssertForbiddenEverywhereAsync(client, "Forged Claims Book");
    }

    [Fact]
    public async Task ForgedServiceMantleClaims_AreReplacedForARealAdministrator()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var forged = MintToken(
            new Claim("sub", "real-admin"),
            new Claim("name", "Real Admin"),
            new Claim("role", "admin"),
            new Claim("servicemantle.operator_id", "attacker"),
            new Claim("servicemantle.operator_source", "system"),
            new Claim("servicemantle.permission", "management.read"));
        using var client = CreateBearerClient(factory, forged);

        // The real role wins with the mapped identity, not the forged one.
        await AssertAdministratorReadAndWriteAsync(client, "Replaced Claims Book");

        // The session response still comes from the original claims only.
        using var session = await client.GetAsync("/admin/auth/session", Ct);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var body = JsonDocument.Parse(await session.Content.ReadAsStringAsync(Ct));
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("Real Admin", data.GetProperty("username").GetString());
        Assert.Equal(
            ["admin"],
            data.GetProperty("roles").EnumerateArray()
                .Select(role => role.GetString()).ToArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("padded")]
    [InlineData("control-character")]
    [InlineData("over-long")]
    public async Task SubjectNotInOperatorWireForm_FailsClosed(string shape)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var claims = new List<Claim> { new("name", "Malformed Subject"), new Claim("role", "admin") };
        switch (shape)
        {
            case "padded":
                claims.Add(new Claim("sub", " padded-admin "));
                break;
            case "control-character":
                // A control character is never in the cleaned operator-id wire
                // form, so the ServiceMantle parser must refuse it.
                claims.Add(new Claim("sub", "bad" + (char)1 + "id"));
                break;
            case "over-long":
                claims.Add(new Claim("sub", new string('x', 300)));
                break;
        }

        using var client = CreateBearerClient(factory, MintToken([.. claims]));

        // The subject is projected verbatim onto the operator-id claim and the
        // ServiceMantle parser refuses it, so the policy fails closed instead
        // of truncating or rewriting the subject.
        using var response = await client.GetAsync("/admin/auth/session", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void ServiceLayerPolicyName_MatchesTheServiceMantleDefault()
    {
        Assert.Equal(
            ManagementAuthorizationDefaults.AdminPolicyName,
            AdminEndpointAuthorization.PolicyName);
    }

    [Fact]
    public void RegisteredPolicies_UseTheServiceMantleNameOnly()
    {
        using var factory = new VocabularyWebApplicationFactory();

        var policies = factory.Services
            .GetRequiredService<IOptions<AuthorizationOptions>>()
            .Value;

        Assert.NotNull(policies.GetPolicy(ManagementAuthorizationDefaults.AdminPolicyName));
        // The retired Lexarbor policy is gone: no code path registers it.
        Assert.Null(policies.GetPolicy("VocabularyAdmin"));
    }
}
