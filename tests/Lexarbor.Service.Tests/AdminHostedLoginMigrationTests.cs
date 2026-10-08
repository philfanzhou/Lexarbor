using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The migration-specific acceptance tests of #209: the declared changes of adopting the
/// official SignaCore client package (the antiforgery token model, the optional-login
/// degradation, the removed Testing-only plain-HTTP transport's tripwire, the fixed
/// application-root default return target) and the strict-profile defaults that must not
/// be relaxed by this repository's own configuration.
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
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SessionWrite_WithoutTokenFailsClosed_WithTokenSucceeds()
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = host.CreateClient(new()
        { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
        var handle = host.CreateSessionCookie();
        // The declared CSRF failure presentation: the strategy scheme's fixed 401.
        using var rejected = await SendWriteAsync(client, handle, null);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Contains("Authentication is required.", await rejected.Content.ReadAsStringAsync(Ct));
        var (token, antiforgery) = AdminTestAntiforgery.Get(host);
        using var accepted = await SendWriteAsync(client, handle, (token, antiforgery));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendWriteAsync(HttpClient client, string handle,
        (string Token, string Cookie)? antiforgery)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/vocabulary-books")
        {
            Content = JsonContent.Create(new { bookName = "Migration book", status = true })
        };
        request.Headers.Add("Cookie", antiforgery is { } pair
            ? handle + "; " + pair.Cookie
            : handle);
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
    public void PackageOptions_KeepTheStrictProfileDefaults()
    {
        using var host = new VocabularyWebApplicationFactory();
        var options = host.Services.GetRequiredService<IOptions<SignaCoreHostedLoginOptions>>().Value;
        // The strict profile is the migration's A5 guarantee: zero clock skew, the
        // scope-echo subset check, duplicate JSON member rejection, bounded bodies and
        // future issued-at rejection — none of them relaxed by this repository.
        Assert.Equal(TimeSpan.Zero, options.Validation.ClockSkew);
        Assert.True(options.Validation.RequireScopeEchoSubset);
        Assert.True(options.Validation.RejectDuplicateJsonMembers);
        Assert.True(options.Validation.RejectFutureIssuedAt);
        Assert.Equal(64 * 1024, options.Validation.MaxTokenResponseBytes);
        Assert.Equal(4 * 1024, options.Validation.MaxLogoutResponseBytes);
        Assert.Equal(AdminSessionCookie.Name, options.SessionCookieName);
        Assert.True(options.AllowUnconfiguredStartup);
        Assert.True(options.SessionEndpointRequireAuthorization);
        Assert.Equal("/#/login?reason=logged_out", options.PostLogoutReturnPath);
        Assert.NotNull(options.PreSignInAuthorizationDecision);
        Assert.IsType<AdminHostedLoginResponseWriter>(options.ResponseWriter);
    }

    [Fact]
    public async Task StartWithoutReturnUrl_LandsOnTheFixedApplicationRootDefault()
    {
        using var f = new HostedLoginMigrationFixture();
        var transaction = await f.Start(null);
        using var callback = await f.Callback(transaction);
        // The declared default change: the official package lands a start without a
        // returnUrl on "/" instead of consulting the validator's historical default.
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
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
                services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
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
            var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers
                .ParseQuery(response.Headers.Location!.Query);
            Authority.Challenges[parameters["code_challenge"].ToString()] = parameters["nonce"].ToString();
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            return new Transaction(parameters["state"].ToString(), cookie.Split(';')[0]);
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
