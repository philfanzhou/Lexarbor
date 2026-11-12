using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The migration-specific acceptance tests: the declared constants of moving the hosted login
/// onto the framework's own OpenID Connect handler (the historical antiforgery token model,
/// the optional-login degradation, the removed Testing-only plain-HTTP transport's tripwire,
/// the fixed application-root default return target) and the strict-profile settings that must
/// not be relaxed by this repository's own configuration.
/// </summary>
public class AdminHostedLoginMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CsrfEndpoint_IsPublicAndIssuesTheTokenModel()
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = host.CreateClient(new()
        { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
        using var response = await client.GetAsync("/admin/auth/csrf", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var token = body.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionWrite_WithoutTokenFailsClosed_WithTokenSucceeds()
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = host.CreateClient(new()
        { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
        var cookie = host.CreateSessionCookie();
        // The declared CSRF failure presentation: the session boundary's fixed 401.
        using var rejected = await SendWriteAsync(client, cookie, null);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Contains("Authentication is required.", await rejected.Content.ReadAsStringAsync(Ct));
        var (token, antiforgery) = AdminTestAntiforgery.Get(host);
        using var accepted = await SendWriteAsync(client, cookie, (token, antiforgery));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendWriteAsync(HttpClient client, string cookie,
        (string Token, string Cookie)? antiforgery)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/vocabulary-books")
        {
            Content = JsonContent.Create(new { bookName = "Migration book", status = true })
        };
        request.Headers.Add("Cookie", antiforgery is { } pair
            ? cookie + "; " + pair.Cookie
            : cookie);
        if (antiforgery is not null) request.Headers.Add(AdminTestAntiforgery.HeaderName, antiforgery.Value.Token);
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task UnconfiguredStartup_KeepsServing_WithFixedDegradedAnswers()
    {
        using var host = new VocabularyWebApplicationFactory("Testing", includeAppCredentials: false);
        using var client = host.CreateClient(new()
        { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
        // The host runs; the public API and health stay available.
        using var health = await client.GetAsync("/health", Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var start = await client.GetAsync("/admin/auth/start", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Contains("Hosted authentication is not configured.", await start.Content.ReadAsStringAsync(Ct));
        using var session = await client.GetAsync("/admin/auth/session", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Contains("Authentication is required.", await session.Content.ReadAsStringAsync(Ct));
        // Logout is local-only and still needs its antiforgery token.
        var (token, antiforgery) = AdminTestAntiforgery.Get(host);
        var logout = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        logout.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        logout.Headers.Add("Cookie", antiforgery);
        using var localOnly = await client.SendAsync(logout, Ct);
        Assert.Equal(HttpStatusCode.OK, localOnly.StatusCode);
        Assert.Equal("{\"success\":true}", await localOnly.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task RemovedHttpTestOriginsSetting_FailsStartupWithTheFixedDiagnostic()
    {
        using var host = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:HttpTestOrigins"] = "http://192.168.50.10:5008" });
        // The tripwire fails host construction: the test host surfaces the startup
        // failure as its own disposal, so the assertion is on the failure itself — the
        // fixed diagnostic (AdminAuthenticationOptions.RemovedSettingFailureMessage,
        // reported through the host's critical startup path in a real process) names the
        // removed setting and the replacement loopback/HTTPS origins.
        Assert.True(Assert.ThrowsAny<Exception>(() =>
        {
            using var client = host.CreateClient();
            using var probe = client.GetAsync("/health/live", Ct).GetAwaiter().GetResult();
            return probe;
        }) is OptionsValidationException or InvalidOperationException);
        Assert.StartsWith("AdminAuthentication:HttpTestOrigins no longer exists",
            AdminAuthenticationOptions.RemovedSettingFailureMessage);
        await Task.CompletedTask;
    }

    [Fact]
    public void HandlerOptions_KeepTheHistoricalProfile()
    {
        // A configured deployment: the options below are the per-deployment protocol values.
        using var f = new HostedLoginMigrationFixture();
        var cookie = f.Host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuthenticationSource.SessionScheme);
        var oidc = f.Host.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AdminAuthenticationSource.OidcScheme);
        // The historical byte-for-byte cookie names and header of the browser contract, the
        // exact callback and return paths, and the strict-profile settings carried over from
        // the replaced slice: no telemetry parameters, the five-minute handshake window, the
        // 30-second clock tolerance, code + PKCE S256, and no pushed authorization.
        Assert.Equal(AdminSessionCookie.Name, cookie.Cookie.Name);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal("/admin/auth/callback", oidc.CallbackPath.Value);
        Assert.Equal("/admin/auth/logout/return", oidc.SignedOutCallbackPath.Value);
        Assert.Equal(AdminHostedLoginWriter.PostLogoutReturnTarget, oidc.SignedOutRedirectUri);
        Assert.True(oidc.DisableTelemetry);
        Assert.True(oidc.UsePkce);
        Assert.Equal("code", oidc.ResponseType);
        Assert.Equal("query", oidc.ResponseMode);
        Assert.Equal(TimeSpan.FromMinutes(5), oidc.RemoteAuthenticationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), oidc.TokenValidationParameters.ClockSkew);
        Assert.Equal("name", oidc.TokenValidationParameters.NameClaimType);
        Assert.Equal("role", oidc.TokenValidationParameters.RoleClaimType);
        Assert.Equal(Microsoft.AspNetCore.Authentication.OpenIdConnect.PushedAuthorizationBehavior.Disable, oidc.PushedAuthorizationBehavior);
        // The antiforgery write model keeps the historical request header the SPA sends.
        Assert.Equal("X-SignaCore-CSRF", f.Host.Services.GetRequiredService<
            IOptionsMonitor<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>>().CurrentValue.HeaderName);
    }

    [Fact]
    public async Task StartWithoutReturnUrl_LandsOnTheFixedApplicationRootDefault()
    {
        using var f = new HostedLoginMigrationFixture();
        var transaction = await f.Start(null);
        using var callback = await f.Callback(transaction);
        // The declared default change of the package era, kept: a start without a
        // returnUrl lands on "/" instead of consulting the validator's historical default.
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/settings/speech")]
    public async Task SettingsReturnTarget_CompletesHostedCallback(string target)
    {
        using var f = new HostedLoginMigrationFixture();
        var transaction = await f.Start(target);
        using var callback = await f.Callback(transaction);
        Assert.Equal("/#" + target, callback.Headers.Location!.OriginalString);
    }

    private sealed class HostedLoginMigrationFixture : IDisposable
    {
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client { get; }
        public SignaCoreAuthorityStub Authority { get; } = new(TimeProvider.System);

        public HostedLoginMigrationFixture()
        {
            var config = new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = SignaCoreAuthorityStub.Issuer,
                ["IdentityService:Issuer"] = SignaCoreAuthorityStub.Issuer,
                ["IdentityService:Audience"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = SignaCoreAuthorityStub.Secret,
                ["AdminAuthentication:OidcCode:RedirectUri"] = SignaCoreAuthorityStub.Redirect,
                ["AdminAuthentication:OidcCode:Scope"] = "openid profile",
                ["RateLimits:AdminLogin:Enabled"] = "false"
            };
            Base = new VocabularyWebApplicationFactory("Testing", true, "OidcCode", config);
            Host = Base.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.Authority = null; options.MetadataAddress = null!; options.ConfigurationManager = null!;
                        options.TokenValidationParameters.IssuerSigningKey = Authority.SigningKey;
                    });
                services.AddHttpClient(AdminAuthenticationSource.OidcHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Authority);
            }));
            Client = Host.CreateClient(new()
            { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
        }

        public async Task<Transaction> Start(string? target)
        {
            using var response = await Client.GetAsync(
                "/admin/auth/start" + (target is null ? "" : "?returnUrl=" + Uri.EscapeDataString(target)), Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var parameters = QueryHelpers
                .ParseQuery(response.Headers.Location!.Query);
            Authority.Challenges[parameters["code_challenge"].ToString()] = parameters["nonce"].ToString();
            var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
            return new Transaction(parameters["state"].ToString(), cookie);
        }

        public Task<HttpResponseMessage> Callback(Transaction t) =>
            Client.SendAsync(CreateCallbackRequest(t), Ct);

        private HttpRequestMessage CreateCallbackRequest(Transaction t)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                "/admin/auth/callback?state=" + t.State + "&iss=" +
                Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer) + "&code=" + SignaCoreAuthorityStub.Code);
            request.Headers.Add("Cookie", t.Cookie);
            return request;
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
            Base.Dispose();
            Authority.Dispose();
        }

        public sealed record Transaction(string State, string Cookie);
    }
}
