using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The Testing-only private-network HTTP transport, end to end: the startup gates,
/// the HttpTest- cookie names without Secure, the full hosted login/logout trip
/// over a plain-HTTP issuer and callback, the isolation of leftover HTTPS cookie
/// names, and the allowlist boundaries of both callback settings.
/// </summary>
public class AdminHttpTestHostedLoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string LoginCookiePrefix = "HttpTest-Lexarbor.Login.";
    private const string LogoutCookiePrefix = "HttpTest-Lexarbor.Logout.";
    private const string SessionCookieName = "HttpTest-Lexarbor.AdminSession";

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void NonTestingEnvironmentWithOrigins_FailsStartup(string environment)
    {
        var logs = new CriticalLogs();
        using var baseFactory = new VocabularyWebApplicationFactory(
            environment,
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["AdminAuthentication:HttpTestOrigins"] = "http://192.168.50.10:5008"
            });
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));

        // A deployment that configures the transport outside Testing must not start
        // while silently keeping the HTTPS contract it actually runs. The submitted
        // value is not echoed.
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(logs.Messages, message =>
            message.Contains("AdminAuthentication:HttpTestOrigins", StringComparison.Ordinal)
            && message.Contains("Testing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://example.com:5008")]
    [InlineData("http://8.8.8.8:5008")]
    [InlineData("http://192.168.50.10")]
    [InlineData("https://192.168.50.10:5008;http://10.0.0.1:5008")]
    public void TestingWithInvalidOrigins_FailsStartup(string origins)
    {
        var logs = new CriticalLogs();
        using var baseFactory = new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:HttpTestOrigins"] = origins });
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(logs.Messages, message =>
            message.Contains("AdminAuthentication:HttpTestOrigins", StringComparison.Ordinal)
            && message.Contains("exact private-IP HTTP origins", StringComparison.Ordinal));
    }

    [Fact]
    public void TestingWithValidOrigins_StartsWithLoudWarning()
    {
        var logs = new CriticalLogs();
        using var baseFactory = new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:HttpTestOrigins"] = "http://192.168.50.10:5008" });
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();

        // The deliberate downgrade is a startup fact, not a quiet one.
        Assert.Contains(logs.Messages, message =>
            message.Contains("plain-HTTP test origins", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HttpTestCodeFlow_CompletesLoginSessionAdminApiLogoutAndReturn()
    {
        using var f = new Fixture();
        var t = await f.Start();
        Assert.StartsWith(LoginCookiePrefix, t.Cookie.Split('=')[0], StringComparison.Ordinal);
        Assert.Contains("httponly", t.SetCookie); Assert.Contains("samesite=lax", t.SetCookie);
        Assert.Contains("path=/", t.SetCookie); Assert.Contains("max-age=300", t.SetCookie);
        Assert.DoesNotContain("secure", t.SetCookie); Assert.DoesNotContain("domain=", t.SetCookie);
        Assert.Equal(Fixture.Redirect, t.Parameters["redirect_uri"]);
        using var callback = await f.Send(f.Query(t), t.Cookie);
        Assert.Equal("/#/books", callback.Headers.Location!.OriginalString);
        var sessionCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(s => s.StartsWith(SessionCookieName + "=", StringComparison.Ordinal));
        Assert.DoesNotContain("secure", sessionCookie);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), s =>
            s.StartsWith(t.Cookie.Split('=')[0] + "=;", StringComparison.Ordinal));
        var cookie = sessionCookie.Split(';')[0];
        using var session = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var read = await f.Send("/admin/vocabulary-books", cookie);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await f.Send("/admin/vocabulary-books", cookie, "POST",
            JsonContent.Create(new { bookName = "HTTP test book", status = true }), csrf: true);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal(Fixture.Redirect, f.Form["redirect_uri"]);
        Assert.Equal(1, f.Posts);
        // Logout: the HttpTest session cookie and the legacy name are cleared, the
        // return transaction is a HttpTest- cookie without Secure, and the return
        // trip completes exactly once.
        using var logout = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var cookies = logout.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, c => c.StartsWith(SessionCookieName + "=;", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith(VocabularyWebApplicationFactory.CookieName + "=;", StringComparison.Ordinal));
        var raw = Assert.Single(cookies, c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal));
        Assert.Contains("httponly", raw); Assert.Contains("samesite=lax", raw); Assert.Contains("path=/", raw);
        Assert.DoesNotContain("secure", raw); Assert.DoesNotContain("domain=", raw);
        Assert.Equal(5, f.LogoutForm.Count);
        Assert.Equal(Fixture.PostLogoutRedirect, f.LogoutForm["post_logout_redirect_uri"]);
        var pair = raw.Split(';')[0];
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        using var back = await f.Return("?state=" + f.LogoutForm["state"], pair);
        Assert.Equal("/#/login?reason=logged_out", back.Headers.Location!.OriginalString);
        using var replay = await f.Return("?state=" + f.LogoutForm["state"], pair);
        Assert.Equal("/#/login?reason=logout_failed", replay.Headers.Location!.OriginalString);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task HttpTestMode_IgnoresHttpsCookieNamesAndViceVersaIsolation()
    {
        using var f = new Fixture();
        // A browser that still carries an HTTPS-name session cookie from a previous
        // HTTPS deployment is not authenticated over the HTTP test names: switching
        // modes requires signing in again.
        var handle = await f.Seed();
        using var session = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + handle);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        // The legacy JWT cookie authenticates nothing in either mode.
        using var legacy = await f.Send("/admin/auth/session",
            VocabularyWebApplicationFactory.CookieName + "=" + f.Token("JWT", f.Claims()));
        Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
    }

    [Fact]
    public async Task OutsideAllowlist_RedirectConfigurationIsRefused()
    {
        using var f = new Fixture(new Dictionary<string, string?>
        {
            ["AdminAuthentication:OidcCode:RedirectUri"] = "http://10.99.99.99:5008/admin/auth/callback"
        });
        using var response = await f.Client.GetAsync("/admin/auth/start", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task OutsideAllowlist_PostLogoutIsSentOnlyWhenListed()
    {
        using var f = new Fixture(new Dictionary<string, string?>
        {
            ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = "http://10.99.99.99:5008/admin/auth/logout/return"
        });
        var cookie = await f.SignIn();
        using var logout = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        // The unlisted HTTP return never reaches the provider; the logout degrades
        // to the return-less upstream form.
        Assert.Equal(3, f.LogoutForm.Count);
        Assert.DoesNotContain("post_logout_redirect_uri", f.LogoutForm.Keys);
        Assert.DoesNotContain(logout.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal));
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
    }

    [Theory]
    [InlineData("access_denied", "canceled")]
    [InlineData("server_error", "provider_unavailable")]
    public async Task HttpTestUpstreamErrors_KeepFixedReasonsAndConsumeOnlyOwnCookie(string error, string reason)
    {
        using var f = new Fixture();
        var t = await f.Start();
        var other = await f.Start();
        using var response = await f.Send(f.Query(t, error), t.Cookie + "; " + other.Cookie);
        Assert.Equal("/#/login?reason=" + reason, response.Headers.Location!.OriginalString);
        Assert.Equal(0, f.Posts);
        using var independent = await f.Send(f.Query(other), other.Cookie);
        Assert.Equal("/#/books", independent.Headers.Location!.OriginalString);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task HttpTestCallback_WrongBrowserFailsClosed()
    {
        using var f = new Fixture();
        var t = await f.Start();
        var query = f.Query(t).Replace(t.Cookie.Split('=')[1], Fixture.Code, StringComparison.Ordinal);
        using var response = await f.Send(query, t.Cookie.Split('=')[0] + "=" + Fixture.Code);
        Assert.Equal("/#/login?reason=sign_in_failed", response.Headers.Location!.OriginalString);
        Assert.Equal(0, f.Posts);
        // The real browser can still complete its own transaction.
        using var valid = await f.Send(f.Query(t), t.Cookie);
        Assert.Equal("/#/books", valid.Headers.Location!.OriginalString);
        f.AssertSafeLogs();
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
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => logs.Messages.Add(logLevel + ": " + formatter(state, exception));
        }
    }

    private sealed record Transaction(string State, string Cookie, string SetCookie, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Parameters);

    private sealed class Fixture : IDisposable
    {
        public const string Issuer = "http://192.168.50.10:5002";
        public const string Origin = "http://192.168.50.10:5008";
        public const string Redirect = Origin + "/admin/auth/callback?registered=1";
        public const string PostLogoutRedirect = Origin + "/admin/auth/logout/return";
        public const string Secret = "synthetic-hosted-secret-marker";
        public static readonly string Code = WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes("synthetic-code-marker-0123456789"));
        public static readonly string Handle = WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes("synthetic-logout-handle-01234567"));
        private readonly RSA _rsa = RSA.Create(2048);
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client { get; }
        public Metadata Manager { get; }
        public Clock Clock { get; } = new();
        public Logs Logs { get; } = new();
        public string LastAccess { get; private set; } = "";
        public string LastId { get; private set; } = "";
        public ConcurrentDictionary<string, string> Challenges { get; } = new();
        private int _posts;
        public int Posts => _posts;
        private int _logoutPosts;
        public int LogoutPosts => _logoutPosts;
        public Dictionary<string, string> Form { get; private set; } = new();
        public Dictionary<string, string> LogoutForm { get; private set; } = new();

        public Fixture(Dictionary<string, string?>? extra = null)
        {
            var config = new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = Issuer,
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = "client-id",
                ["IdentityService:RequireHttpsMetadata"] = "false",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = Secret,
                ["AdminAuthentication:OidcCode:RedirectUri"] = Redirect,
                ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = PostLogoutRedirect,
                ["AdminAuthentication:OidcCode:Scope"] = "openid profile",
                ["AdminAuthentication:HttpTestOrigins"] = Origin,
                ["RateLimits:AdminLogin:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace"
            };
            if (extra is not null) foreach (var pair in extra) config[pair.Key] = pair.Value;
            Manager = new Metadata(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "key-1" });
            Base = new VocabularyWebApplicationFactory("Testing", true, "OidcCode", config);
            Host = Base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    { options.ConfigurationManager = Manager; options.TokenValidationParameters.IssuerSigningKey = Manager.Key; });
                    services.AddHttpClient(AdminCodeExchange.BackchannelName).ConfigurePrimaryHttpMessageHandler(() => new TokenHandler(this));
                    services.AddHttpClient(AdminPreparedLogout.BackchannelName).ConfigurePrimaryHttpMessageHandler(() => new LogoutHandler(this));
                });
            });
            Client = Host.CreateClient(new() { BaseAddress = new Uri(Origin), AllowAutoRedirect = false, HandleCookies = false });
        }

        public async Task<Transaction> Start()
        {
            using var response = await Client.GetAsync("/admin/auth/start", Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            var state = parameters["state"].ToString();
            Challenges[parameters["code_challenge"].ToString()] = parameters["nonce"].ToString();
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            return new(state, cookie.Split(';')[0], cookie, parameters);
        }

        public string Query(Transaction t, string? error = null) => "/admin/auth/callback?registered=1&state=" + t.State
            + "&iss=" + Uri.EscapeDataString(Issuer)
            + (error is null ? "&code=" + Code : "&error=" + error);

        public async Task<string> SignIn()
        {
            var t = await Start();
            using var callback = await Send(Query(t), t.Cookie);
            Assert.Equal("/#/books", callback.Headers.Location!.OriginalString);
            return callback.Headers.GetValues("Set-Cookie")
                .Single(s => s.StartsWith(SessionCookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        }

        public Task<HttpResponseMessage> Logout(string cookie = "", bool csrf = true)
            => Send("/admin/auth/logout", cookie, "POST", null, csrf);

        public Task<HttpResponseMessage> Return(string query, string cookie = "")
            => Send("/admin/auth/logout/return" + query, cookie);

        public Task<HttpResponseMessage> Send(string path, string cookie = "", string method = "GET", HttpContent? body = null, bool csrf = false)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
            if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
            if (csrf) request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return Client.SendAsync(request, Ct);
        }

        public async Task<string> Seed(string? idToken = "synthetic-old-id-marker")
        {
            using var scope = Host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
            { AccessToken = "synthetic-old-access-marker", IdToken = idToken, Issuer = Issuer, Subject = "old-sub", DisplayName = "old-user", Roles = ["admin"], AccessTokenExpiresAt = Clock.Now.AddMinutes(15) }, Ct);
        }

        public Dictionary<string, object> Claims() => new()
        { ["iss"] = Issuer, ["aud"] = "client-id", ["sub"] = "account-42", ["iat"] = Clock.Now.ToUnixTimeSeconds() - 60, ["exp"] = Clock.Now.ToUnixTimeSeconds() + 900, ["name"] = "access-user", ["role"] = "admin" };

        public string Token(string type, Dictionary<string, object> claims)
        {
            var header = new Dictionary<string, object> { ["alg"] = "RS256", ["kid"] = "key-1", ["typ"] = type };
            var signing = Base64UrlEncoder.Encode(JsonSerializer.Serialize(header)) + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims));
            var signature = _rsa.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return signing + "." + Base64UrlEncoder.Encode(signature);
        }

        public void AssertSafeLogs()
        {
            foreach (var marker in new[] { Code, Secret, LastAccess, LastId, Handle,
                Form.GetValueOrDefault("code_verifier", ""), LogoutForm.GetValueOrDefault("state", ""),
                "sensitive-old-access-marker", "sensitive-old-id-marker" })
                if (marker.Length > 0) Assert.DoesNotContain(Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
        }

        public void Dispose() { Client.Dispose(); Host.Dispose(); Base.Dispose(); _rsa.Dispose(); }

        private static Dictionary<string, string> Parse(string body) => body
            .Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));

        private sealed class TokenHandler(Fixture f) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref f._posts);
                Assert.Equal(Issuer + "/token", request.RequestUri!.AbsoluteUri);
                Assert.Null(request.Headers.Authorization);
                f.Form = Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(f.Form["code_verifier"])));
                var nonce = f.Challenges[challenge];
                var id = f.Claims(); id.Remove("role"); id["exp"] = f.Clock.Now.ToUnixTimeSeconds() + 300; id["name"] = "id-user"; id["nonce"] = nonce;
                var accessToken = f.Token("at+jwt", f.Claims());
                var idToken = f.Token("JWT", id);
                f.LastAccess = accessToken;
                f.LastId = idToken;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object>
                    { ["access_token"] = accessToken, ["id_token"] = idToken, ["token_type"] = "Bearer", ["expires_in"] = 900, ["scope"] = "openid profile" })
                });
            }
        }

        private sealed class LogoutHandler(Fixture f) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref f._logoutPosts);
                Assert.Equal(Issuer + AdminPreparedLogout.LogoutRequestPath, request.RequestUri!.AbsoluteUri);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Null(request.Headers.Authorization);
                f.LogoutForm = Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                // SignaCore's documented success shape: a relative reference resolved
                // against the trusted plain-HTTP issuer.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"logout_uri\":" + JsonSerializer.Serialize("/oauth2/logout?logout_handle=" + Handle) + "}", Encoding.UTF8, "application/json") });
            }
        }
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Metadata(SecurityKey key) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public SecurityKey Key { get; } = key;
        public OpenIdConnectConfiguration Configuration { get; } = Published(key);
        private static OpenIdConnectConfiguration Published(SecurityKey key)
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Fixture.Issuer, AuthorizationEndpoint = Fixture.Issuer + "/authorize", TokenEndpoint = Fixture.Issuer + "/token", JwksUri = Fixture.Issuer + "/jwks" };
            configuration.SigningKeys.Add(key);
            return configuration;
        }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        { cancel.ThrowIfCancellationRequested(); return Task.FromResult(Configuration); }
        public void RequestRefresh() { }
    }

    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, Messages);
        public void Dispose() { }
        private sealed class Recorder(string category, ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull { messages.Add(category + " scope: " + Render(state)); return null; }
            private static string Render<T>(T state) => state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(pair => pair.Key + "=" + pair.Value)) : state?.ToString() ?? "";
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(category + ": " + formatter(state, exception) + " structured: " + Render(state) + " exception: " + exception);
        }
    }
}
