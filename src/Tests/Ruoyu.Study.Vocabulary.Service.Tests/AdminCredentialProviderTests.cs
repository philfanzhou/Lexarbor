using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Vocabulary.Host.Authentication;
using Ruoyu.Study.Vocabulary.Host.Authentication.Providers;
using Ruoyu.Study.Vocabulary.Service.Tests.TestInfrastructure;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

/// <summary>
/// Exercises the credential provider seam. The QuantumZhou provider keeps the existing
/// proprietary contract; the OIDC provider proves the abstraction is wide enough for a
/// second, structurally different protocol (form encoding, snake_case, RFC 6749 error
/// semantics) without touching anything downstream of the token.
/// </summary>
public class AdminCredentialProviderTests
{
    [Fact]
    public void DefaultProvider_IsQuantumZhou()
    {
        using var factory = new VocabularyWebApplicationFactory();

        using var scope = factory.Services.CreateScope();
        var authenticator = scope.ServiceProvider
            .GetRequiredService<IAdminCredentialAuthenticator>();

        authenticator.Should().BeOfType<QuantumZhouIdentityAuthenticator>();
    }

    [Fact]
    public void OidcProvider_IsRegisteredWhenSelected()
    {
        using var factory = CreateOidcFactory();

        using var scope = factory.Services.CreateScope();
        var authenticator = scope.ServiceProvider
            .GetRequiredService<IAdminCredentialAuthenticator>();

        authenticator.Should().BeOfType<OidcPasswordAuthenticator>();
    }

    [Fact]
    public async Task OidcProvider_SuccessfulLogin_SetsCookieAndPostsPasswordGrant()
    {
        using var factory = CreateOidcFactory();
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Single()
            .Should().Contain(VocabularyWebApplicationFactory.CookieName);

        factory.Identity.LastContentType.Should().Be("application/x-www-form-urlencoded");
        factory.Identity.LastRequestUri.Should()
            .Be("http://identity.test/protocol/openid-connect/token");

        var form = HttpUtility.ParseQueryString(factory.Identity.LastRequestBody!);
        form["grant_type"].Should().Be("password");
        form["username"].Should().Be("admin");
        form["password"].Should().Be("test-password");
        form["client_id"].Should().Be("vocabulary-client");
        form["client_secret"].Should().Be("vocabulary-client-secret");

        // The proprietary headers belong to the other provider only.
        factory.Identity.LastAppId.Should().BeNull();
        factory.Identity.LastAppSecret.Should().BeNull();
    }

    [Fact]
    public async Task OidcProvider_InvalidGrant_Returns401WithoutCookie()
    {
        using var factory = CreateOidcFactory();
        factory.Identity.Mode = FakeIdentityMode.InvalidCredentials;
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Fact]
    public async Task OidcProvider_Unreachable_Returns502()
    {
        using var factory = CreateOidcFactory();
        factory.Identity.Mode = FakeIdentityMode.Unavailable;
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task OidcProvider_NonAdminToken_Returns403WithoutCookie()
    {
        using var factory = CreateOidcFactory();
        factory.Identity.AccessToken = factory.CreateToken("student");
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Should().NotContainKey("Set-Cookie");
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

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
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

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // FakeIdentityHandler reports this userId in its envelope; the JWT does not
        // carry it as a display name, so it must not surface.
        body.Should().NotContain("identity-user");
        using var document = JsonDocument.Parse(body);
        document.RootElement
            .GetProperty("data")
            .GetProperty("username")
            .GetString()
            .Should().Be("test-user");
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
