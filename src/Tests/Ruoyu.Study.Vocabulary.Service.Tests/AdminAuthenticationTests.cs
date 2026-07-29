using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Ruoyu.Study.Vocabulary.Service.Tests.TestInfrastructure;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

public class AdminAuthenticationTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private readonly VocabularyWebApplicationFactory _factory;

    public AdminAuthenticationTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Identity.Reset();
        _factory.Identity.AccessToken = _factory.CreateToken("admin");
    }

    [Fact]
    public async Task AdminEndpoint_WithoutToken_Returns401Envelope()
    {
        using var client = CreateClient(_factory);

        var response = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithRegularUserToken_Returns403Envelope()
    {
        using var client = CreateClient(_factory);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("student"));

        var response = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        await AssertFailureAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Login_AdminIdentityResponse_SetsHttpOnlyCookie()
    {
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        setCookie.Should().Contain(VocabularyWebApplicationFactory.CookieName);
        setCookie.ToLowerInvariant().Should().Contain("httponly");
        setCookie.ToLowerInvariant().Should().Contain("samesite=strict");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(_factory.Identity.AccessToken);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("identity-refresh-token");
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401WithoutCookie()
    {
        _factory.Identity.Mode = FakeIdentityMode.InvalidCredentials;
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
        response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Fact]
    public async Task Login_RegularUser_Returns403WithoutCookie()
    {
        _factory.Identity.Mode = FakeIdentityMode.RegularUserSuccess;
        _factory.Identity.AccessToken = _factory.CreateToken("student");
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        await AssertFailureAsync(response, HttpStatusCode.Forbidden);
        response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Fact]
    public async Task Login_MissingProductionAppCredentials_Returns503()
    {
        await using var factory = new VocabularyWebApplicationFactory(
            environment: "Production",
            includeAppCredentials: false);
        using var client = CreateClient(factory);

        var response = await LoginAsync(client);

        await AssertFailureAsync(response, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Login_IdentityUnavailable_Returns502()
    {
        _factory.Identity.Mode = FakeIdentityMode.Unavailable;
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        await AssertFailureAsync(response, HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Login_InvalidIdentityAccessToken_Returns502WithoutCookie()
    {
        _factory.Identity.AccessToken = "not-a-valid-jwt";
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        await AssertFailureAsync(response, HttpStatusCode.BadGateway);
        response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Fact]
    public async Task Login_ForwardsCamelCaseBodyAndServerCredentials()
    {
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(_factory.Identity.LastRequestBody!);
        body.RootElement.GetProperty("grantType").GetString().Should().Be("password");
        body.RootElement.GetProperty("username").GetString().Should().Be("admin");
        body.RootElement.GetProperty("password").GetString().Should().Be("test-password");
        body.RootElement.TryGetProperty("GrantType", out _).Should().BeFalse();
        _factory.Identity.LastAppId.Should().Be("vocabulary-app");
        _factory.Identity.LastAppSecret.Should().Be("vocabulary-secret");
    }

    [Fact]
    public async Task AdminCookie_AllowsBookAndVocabularyManagementReads()
    {
        using var client = CreateClient(_factory);
        (await LoginAsync(client)).EnsureSuccessStatusCode();

        var books = await client.GetAsync(
            "/admin/vocabulary-books?keyword=test&page=1&size=20");
        var categories = await client.GetAsync("/admin/vocabulary-books/categories");

        books.StatusCode.Should().Be(HttpStatusCode.OK);
        categories.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_DeletesCookieAndSubsequentAdminRequestReturns401()
    {
        using var client = CreateClient(_factory);
        (await LoginAsync(client)).EnsureSuccessStatusCode();

        var logout = await client.PostAsync("/admin/auth/logout", content: null);
        var afterLogout = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = logout.Headers.GetValues("Set-Cookie").Single();
        setCookie.Should().Contain(VocabularyWebApplicationFactory.CookieName);
        setCookie.ToLowerInvariant().Should().Contain("expires=");
        await AssertFailureAsync(afterLogout, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CookieWrite_WithoutRequestedWithHeader_Returns403()
    {
        using var client = CreateClient(_factory);
        (await LoginAsync(client)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = "Protected Book", status = true });

        await AssertFailureAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InvalidCookieWrite_WithoutRequestedWithHeader_Returns401()
    {
        using var client = CreateClient(_factory);
        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{VocabularyWebApplicationFactory.CookieName}=invalid-token");

        var response = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = "Rejected Book", status = true });

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BearerWrite_DoesNotRequireCookieCsrfHeader()
    {
        using var client = CreateClient(_factory);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("admin"));

        var response = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = "Bearer Book", status = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpClient CreateClient(VocabularyWebApplicationFactory factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        return client.PostAsJsonAsync(
            "/admin/auth/login",
            new { username = "admin", password = "test-password" });
    }

    private static async Task AssertFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        response.StatusCode.Should().Be(expectedStatus);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
