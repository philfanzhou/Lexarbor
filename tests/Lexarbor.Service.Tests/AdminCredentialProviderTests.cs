using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Web;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Authentication.Providers;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Exercises the credential provider seam. The Gateway provider keeps the existing
/// proprietary contract; the OIDC provider proves the abstraction is wide enough for a
/// second, structurally different protocol (form encoding, snake_case, RFC 6749 error
/// semantics) without touching anything downstream of the token.
/// </summary>
public class AdminCredentialProviderTests
{
    [Fact]
    public void DefaultProvider_IsOidc()
    {
        var options = new AdminAuthenticationOptions();

        Assert.Equal(AdminAuthenticationProvider.Oidc, options.Provider);
    }

    [Fact]
    public void OidcProvider_IsRegisteredWhenSelected()
    {
        using var factory = CreateOidcFactory();

        using var scope = factory.Services.CreateScope();
        var authenticator = scope.ServiceProvider
            .GetRequiredService<IAdminCredentialAuthenticator>();

        Assert.IsType<OidcPasswordAuthenticator>(authenticator);
    }

    [Fact]
    public async Task OidcProvider_SuccessfulLogin_SetsCookieAndPostsPasswordGrant()
    {
        using var factory = CreateOidcFactory();
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            VocabularyWebApplicationFactory.CookieName,
            response.Headers.GetValues("Set-Cookie").Single());

        Assert.Equal("application/x-www-form-urlencoded", factory.Identity.LastContentType);
        Assert.Equal(
            "http://identity.test/protocol/openid-connect/token",
            factory.Identity.LastRequestUri);

        var form = HttpUtility.ParseQueryString(factory.Identity.LastRequestBody!);
        Assert.Equal("password", form["grant_type"]);
        Assert.Equal("admin", form["username"]);
        Assert.Equal("test-password", form["password"]);
        Assert.Equal("vocabulary-client", form["client_id"]);
        Assert.Equal("vocabulary-client-secret", form["client_secret"]);

        // The proprietary headers belong to the other provider only.
        Assert.Null(factory.Identity.LastAppId);
        Assert.Null(factory.Identity.LastAppSecret);
    }

    [Fact]
    public async Task OidcProvider_InvalidGrant_Returns401WithoutCookie()
    {
        using var factory = CreateOidcFactory();
        factory.Identity.Mode = FakeIdentityMode.InvalidCredentials;
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    /// <summary>
    /// These codes mean Lexarbor's client registration is wrong, not the password:
    /// SignaCore, for one, answers any requested scope with invalid_scope and a wrong
    /// client secret with a 401 invalid_client. Reporting either as a bad password would
    /// send the administrator back to retype credentials that were never the problem.
    /// </summary>
    [Theory]
    [InlineData("invalid_client", HttpStatusCode.Unauthorized)]
    [InlineData("invalid_scope", HttpStatusCode.BadRequest)]
    [InlineData("unauthorized_client", HttpStatusCode.BadRequest)]
    [InlineData("unsupported_grant_type", HttpStatusCode.BadRequest)]
    public async Task OidcProvider_ClientConfigurationError_Returns502WithoutCookie(
        string error,
        HttpStatusCode status)
    {
        using var factory = CreateOidcFactory();
        factory.Identity.Mode = FakeIdentityMode.OidcError;
        factory.Identity.OidcError = error;
        factory.Identity.OidcErrorStatus = status;
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task OidcProvider_UnrecognizedErrorCode_Returns401()
    {
        using var factory = CreateOidcFactory();
        factory.Identity.Mode = FakeIdentityMode.OidcError;
        factory.Identity.OidcError = "account_locked";
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Models the deployment described in Deployment.md: SignaCore rejects any scope, and
    /// an application in per-application audience mode receives tokens whose audience is
    /// its AppId, carrying SignaCore's own claim set.
    /// </summary>
    [Fact]
    public async Task OidcProvider_SignaCoreToken_SignsInWithoutRequestingScope()
    {
        using var factory = CreateSignaCoreFactory();
        factory.Identity.AccessToken = CreateSignaCoreToken(SignaCoreAppId);
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = HttpUtility.ParseQueryString(factory.Identity.LastRequestBody!);
        Assert.Null(form["scope"]);
        Assert.Equal(SignaCoreAppId, form["client_id"]);

        var session = await client.GetAsync(
            "/admin/auth/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var document = JsonDocument.Parse(
            await session.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "Administrator",
            document.RootElement.GetProperty("data").GetProperty("username").GetString());
    }

    /// <summary>
    /// An application left in SignaCore's shared audience mode receives tokens that every
    /// other shared-mode service would also accept; Lexarbor must not.
    /// </summary>
    [Fact]
    public async Task OidcProvider_SignaCoreSharedAudienceToken_Returns502WithoutCookie()
    {
        using var factory = CreateSignaCoreFactory();
        factory.Identity.AccessToken = CreateSignaCoreToken("SignaCore.Services");
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task OidcProvider_Unreachable_Returns502()
    {
        using var factory = CreateOidcFactory();
        factory.Identity.Mode = FakeIdentityMode.Unavailable;
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task OidcProvider_NonAdminToken_Returns403WithoutCookie()
    {
        using var factory = CreateOidcFactory();
        factory.Identity.AccessToken = factory.CreateToken("student");
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task OidcProvider_MissingClientIdInProduction_Returns503()
    {
        using var factory = new VocabularyWebApplicationFactory(
            environment: "Production",
            includeAppCredentials: false,
            provider: "Oidc");
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// The provider's own response envelope must never reach the client: the login body
    /// is built purely from claims in the validated token.
    /// </summary>
    [Fact]
    public async Task Login_DoesNotEchoProviderSuppliedUserInfo()
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // FakeIdentityHandler reports this userId in its envelope; the JWT does not
        // carry it as a display name, so it must not surface.
        Assert.DoesNotContain("identity-user", body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            "test-user",
            document.RootElement
                .GetProperty("data")
                .GetProperty("username")
                .GetString());
    }

    private static VocabularyWebApplicationFactory CreateOidcFactory()
    {
        return new VocabularyWebApplicationFactory(
            environment: "Testing",
            includeAppCredentials: true,
            provider: "Oidc");
    }

    private const string SignaCoreAppId = "lexarbor-admin";

    private static VocabularyWebApplicationFactory CreateSignaCoreFactory()
    {
        return new VocabularyWebApplicationFactory(
            environment: "Testing",
            includeAppCredentials: true,
            provider: "Oidc",
            extraConfiguration: new Dictionary<string, string?>
            {
                ["IdentityService:Audience"] = SignaCoreAppId,
                ["AdminAuthentication:Oidc:ClientId"] = SignaCoreAppId,
                ["AdminAuthentication:Oidc:Scope"] = string.Empty
            });
    }

    /// <summary>
    /// The claim set SignaCore's token issuance writes, with the role its bootstrap
    /// administrator receives.
    /// </summary>
    private static string CreateSignaCoreToken(string audience)
    {
        return VocabularyWebApplicationFactory.MintToken(
            audience,
            [
                new Claim("sub", "0b7d5f0e-8f43-4a36-9d7e-3c1f2a6b9e10"),
                new Claim("name", "Administrator"),
                new Claim("nickname", "Administrator"),
                new Claim("auth_method", "password"),
                new Claim("client_id", SignaCoreAppId),
                new Claim("role", "admin")
            ]);
    }

    private static HttpClient CreateClient(VocabularyWebApplicationFactory factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        return client.PostAsJsonAsync(
            "/admin/auth/login",
            new { username = "admin", password = "test-password" });
    }
}
