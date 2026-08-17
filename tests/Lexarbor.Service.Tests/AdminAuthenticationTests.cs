using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Lexarbor.Service.Tests.TestInfrastructure;

namespace Lexarbor.Service.Tests;

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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains(VocabularyWebApplicationFactory.CookieName, setCookie);
        Assert.Contains("httponly", setCookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", setCookie.ToLowerInvariant());
        var loginBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_factory.Identity.AccessToken, loginBody);
        Assert.DoesNotContain("identity-refresh-token", loginBody);
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401WithoutCookie()
    {
        _factory.Identity.Mode = FakeIdentityMode.InvalidCredentials;
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_RegularUser_Returns403WithoutCookie()
    {
        _factory.Identity.Mode = FakeIdentityMode.RegularUserSuccess;
        _factory.Identity.AccessToken = _factory.CreateToken("student");
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        await AssertFailureAsync(response, HttpStatusCode.Forbidden);
        Assert.False(response.Headers.Contains("Set-Cookie"));
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
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_ForwardsCamelCaseBodyAndServerCredentials()
    {
        using var client = CreateClient(_factory);

        var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(_factory.Identity.LastRequestBody!);
        Assert.Equal("password", body.RootElement.GetProperty("grantType").GetString());
        Assert.Equal("admin", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("test-password", body.RootElement.GetProperty("password").GetString());
        Assert.False(body.RootElement.TryGetProperty("GrantType", out _));
        Assert.Equal("vocabulary-app", _factory.Identity.LastAppId);
        Assert.Equal("vocabulary-secret", _factory.Identity.LastAppSecret);
    }

    [Fact]
    public async Task AdminCookie_AllowsBookAndVocabularyManagementReads()
    {
        using var client = CreateClient(_factory);
        (await LoginAsync(client)).EnsureSuccessStatusCode();

        var books = await client.GetAsync(
            "/admin/vocabulary-books?keyword=test&page=1&size=20");
        var categories = await client.GetAsync("/admin/vocabulary-books/categories");

        Assert.Equal(HttpStatusCode.OK, books.StatusCode);
        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
    }

    [Fact]
    public async Task Logout_DeletesCookieAndSubsequentAdminRequestReturns401()
    {
        using var client = CreateClient(_factory);
        (await LoginAsync(client)).EnsureSuccessStatusCode();

        var logout = await LogoutAsync(client);
        var afterLogout = await client.GetAsync("/admin/vocabulary-books?page=1&size=20");

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var setCookie = logout.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains(VocabularyWebApplicationFactory.CookieName, setCookie);
        Assert.Contains("expires=", setCookie.ToLowerInvariant());
        await AssertFailureAsync(afterLogout, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithoutRequestedWithHeader_Returns403()
    {
        using var client = CreateClient(_factory);
        (await LoginAsync(client)).EnsureSuccessStatusCode();

        var response = await client.PostAsync("/admin/auth/logout", content: null);

        await AssertFailureAsync(response, HttpStatusCode.Forbidden);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    private static Task<HttpResponseMessage> LogoutAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return client.SendAsync(request);
    }

    private static async Task AssertFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("message").GetString()));
    }
}
