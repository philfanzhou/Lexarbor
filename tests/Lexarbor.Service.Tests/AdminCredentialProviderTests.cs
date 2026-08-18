using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Authentication.Providers;
using Lexarbor.Service.Tests.TestInfrastructure;

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
