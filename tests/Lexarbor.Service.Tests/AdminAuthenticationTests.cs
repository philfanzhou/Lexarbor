using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Administrator authentication after the password proxy was removed: hosted login is
/// the only sign-in, the retired password route answers like any unknown admin route,
/// and the legacy JWT cookie authenticates nothing.
/// </summary>
public class AdminAuthenticationTests :
    IClassFixture<VocabularyWebApplicationFactory>
{
    private readonly VocabularyWebApplicationFactory _factory;

    public AdminAuthenticationTests(VocabularyWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminEndpoint_WithoutToken_Returns401Envelope()
    {
        using var client = CreateClient(_factory);

        var response = await client.GetAsync("/admin/vocabulary-books?page=1&size=20",
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithRegularUserToken_Returns403Envelope()
    {
        using var client = CreateClient(_factory);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken("student"));

        var response = await client.GetAsync("/admin/vocabulary-books?page=1&size=20",
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Oidc")]
    [InlineData("Gateway")]
    [InlineData("hosted")]
    [InlineData("password")]
    public void RetiredProviderValues_FailStartupWithMigrationDiagnostic(string provider)
    {
        var logs = new CriticalLogs();
        using var factory = new VocabularyWebApplicationFactory(
            environment: "Testing",
            includeAppCredentials: false,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["AdminAuthentication:Provider"] = provider
            })
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
                logging.AddProvider(logs)));

        // A deployment that still selects a password provider must not start while
        // silently pretending the selection took effect. The entry point ends early
        // (which the test host reports as a disposed provider) and the critical
        // diagnostic names the setting and the migration document; the container
        // test additionally pins the non-zero process exit. The submitted value is
        // deliberately not echoed.
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(logs.Messages, message =>
            message.Contains("AdminAuthentication:Provider", StringComparison.Ordinal)
            && message.Contains("HostedLoginReleaseNotes", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message =>
            message.Contains("vocabulary-test-signing-key", StringComparison.Ordinal));
    }

    private sealed class CriticalLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose() { }

        private sealed class Recorder(CriticalLogs logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Critical)
                {
                    logs.Messages.Add(formatter(state, exception));
                }
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OidcCode")]
    [InlineData("oidccode")]
    public void UnsetOrOidcCodeProvider_StartsNormally(string? provider)
    {
        var configuration = new Dictionary<string, string?>();
        if (provider is not null)
        {
            configuration["AdminAuthentication:Provider"] = provider;
        }
        using var factory = new VocabularyWebApplicationFactory(
            environment: "Testing",
            includeAppCredentials: false,
            extraConfiguration: configuration);

        using var client = factory.CreateClient();
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("/admin/auth/login")]
    [InlineData("/admin/auth/login/")]
    [InlineData("/ADMIN/AUTH/LOGIN")]
    public async Task DeletedLoginRoute_AnonymousRequest_FallsBackToAdmin401(string path)
    {
        using var client = CreateClient(_factory);

        var response = await client.PostAsync(path,
            JsonContent.Create(new { username = "admin", password = "test-password" }),
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeletedLoginRoute_AuthenticatedAdmin_GetsCatchall404()
    {
        using var client = CreateClient(_factory);
        var handle = await SeedSessionAsync(_factory);
        var (token, antiforgery) = AdminTestAntiforgery.Get(_factory);
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/login")
        {
            Content = JsonContent.Create(new { username = "admin", password = "test-password" })
        };
        request.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        request.Headers.TryAddWithoutValidation("Cookie",
            $"{AdminSessionCookie.Name}={handle}; {antiforgery}");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Admin endpoint was not found.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LegacyJwtCookie_NeverAuthenticates()
    {
        using var client = CreateClient(_factory);
        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{VocabularyWebApplicationFactory.CookieName}={_factory.CreateToken("admin")}");

        var response = await client.GetAsync("/admin/auth/session",
            TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SessionCookie_AllowsBookAndVocabularyManagementReads()
    {
        using var client = CreateClient(_factory);
        var handle = await SeedSessionAsync(_factory);
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminSessionCookie.Name}={handle}");

        var books = await client.GetAsync(
            "/admin/vocabulary-books?keyword=test&page=1&size=20",
                TestContext.Current.CancellationToken);
        var categories = await client.GetAsync("/admin/vocabulary-books/categories",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, books.StatusCode);
        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
    }

    [Fact]
    public async Task Logout_DeletesBothCookiesAndSubsequentAdminRequestReturns401()
    {
        using var client = CreateClient(_factory);
        var handle = await SeedSessionAsync(_factory);
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminSessionCookie.Name}={handle}");

        var logout = await LogoutAsync(client, _factory);
        var afterLogout = await client.GetAsync("/admin/vocabulary-books?page=1&size=20",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var cookies = logout.Headers.GetValues("Set-Cookie").ToArray();
        // Both names are deleted; the package's own session deletion may appear
        // beside the middleware's, so the assertion is on the distinct names.
        Assert.Equal(2, cookies.Select(value => value.Split('=')[0]).Distinct().Count());
        foreach (var name in new[] { AdminSessionCookie.Name, VocabularyWebApplicationFactory.CookieName })
            Assert.Contains(cookies, value => value.StartsWith(name + "=;", StringComparison.Ordinal));
        await AssertFailureAsync(afterLogout, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_ReturnsTheCsrfRejection()
    {
        using var client = CreateClient(_factory);
        var handle = await SeedSessionAsync(_factory);
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminSessionCookie.Name}={handle}");

        var response = await client.PostAsync("/admin/auth/logout", content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("csrf_rejected", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CookieWrite_WithoutAntiforgeryToken_Returns401()
    {
        using var client = CreateClient(_factory);
        var handle = await SeedSessionAsync(_factory);
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminSessionCookie.Name}={handle}");

        var response = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = "Protected Book", status = true },
                TestContext.Current.CancellationToken);

        await AssertFailureAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidSessionCookieWrite_Returns401()
    {
        using var client = CreateClient(_factory);
        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{AdminSessionCookie.Name}=invalid-handle");

        var response = await client.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = "Rejected Book", status = true },
                TestContext.Current.CancellationToken);

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
            new { bookName = "Bearer Book", status = true }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> SeedSessionAsync(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
        {
            AccessToken = "synthetic-access-marker",
            IdToken = "synthetic-id-marker",
            Issuer = VocabularyWebApplicationFactory.Issuer,
            Subject = "session-subject",
            DisplayName = "session-user",
            Roles = ["admin"],
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        }, TestContext.Current.CancellationToken);
    }

    private static HttpClient CreateClient(VocabularyWebApplicationFactory factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
    }

    private static async Task<HttpResponseMessage> LogoutAsync(
        HttpClient client, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var (token, antiforgery) = AdminTestAntiforgery.Get(factory);
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        request.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        var cookies = new List<string>();
        if (client.DefaultRequestHeaders.TryGetValues("Cookie", out var existing)) cookies.AddRange(existing);
        cookies.Add(antiforgery);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        return await client.SendAsync(request);
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
