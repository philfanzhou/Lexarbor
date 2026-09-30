using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Lexarbor.Service.Tests;

public class AdminCodeExchangeTests
{
    [Theory]
    [InlineData("pwd", true)]
    [InlineData("sms", false)]
    public async Task Success_OnlyAccessPrincipalAndExactConfidentialForm(string amr, bool name)
    {
        using var f = new Fixture();
        f.Id["amr"] = new[] { amr };
        if (!name) f.Access.Remove("name");
        f.Reply();
        var result = await f.Redeem(TestContext.Current.CancellationToken);
        Assert.Equal(AdminCodeStatus.Success, result.Status);
        Assert.Equal(f.AccessToken, result.AccessToken);
        Assert.Equal(f.IdToken, result.IdToken);
        Assert.Equal(name ? "access-name" : "account-42", VocabularyClaims.GetDisplayName(result.Principal));
        Assert.Equal("account-42", result.Principal!.FindFirst("sub")!.Value);
        Assert.True(VocabularyClaims.HasRole(result.Principal, "admin"));
        Assert.Equal("https://issuer.test/discovered/token", f.RequestUri);
        Assert.Null(f.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", f.ContentType);
        Assert.Equivalent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = Fixture.Code,
            ["code_verifier"] = Fixture.Verifier, ["redirect_uri"] = f.CodeOptions.RedirectUri,
            ["client_id"] = "client-id", ["client_secret"] = "synthetic-secret-marker"
        }, f.Form, strict: true);
        Assert.DoesNotContain(Fixture.Code, result.ToString()!);
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData("id", "typ")][InlineData("access", "typ")]
    [InlineData("id", "alg")][InlineData("access", "alg")]
    [InlineData("id", "signature")][InlineData("access", "signature")]
    [InlineData("id", "kid")][InlineData("access", "kid")]
    [InlineData("id", "issuer")][InlineData("access", "issuer")]
    [InlineData("id", "audience")][InlineData("access", "audience")]
    [InlineData("id", "extra-audience")][InlineData("access", "extra-audience")]
    [InlineData("id", "missing-sub")][InlineData("access", "missing-sub")]
    [InlineData("id", "duplicate-sub")][InlineData("access", "duplicate-sub")]
    [InlineData("id", "empty-sub")][InlineData("access", "empty-sub")]
    [InlineData("id", "missing-iat")][InlineData("access", "missing-iat")]
    [InlineData("id", "missing-exp")][InlineData("access", "missing-exp")]
    [InlineData("id", "future-iat")][InlineData("access", "future-iat")]
    [InlineData("id", "exp-before-iat")][InlineData("access", "exp-before-iat")]
    [InlineData("id", "exact-expiry")][InlineData("access", "exact-expiry")]
    [InlineData("id", "expired-within-skew")][InlineData("access", "expired-within-skew")]
    [InlineData("id", "duplicate-iat")][InlineData("access", "duplicate-iat")]
    [InlineData("id", "duplicate-exp")][InlineData("access", "duplicate-exp")]
    [InlineData("id", "missing-nonce")][InlineData("id", "wrong-nonce")]
    [InlineData("id", "duplicate-nonce")][InlineData("id", "sub-mismatch")]
    public async Task StrictTrust_RejectsEveryIndependentTokenDefect(string kind, string defect)
    {
        using var f = new Fixture();
        var claims = kind == "id" ? f.Id : f.Access;
        var header = kind == "id" ? f.IdHeader : f.AccessHeader;
        switch (defect)
        {
            case "typ": header["typ"] = kind == "id" ? "at+jwt" : "JWT"; break;
            case "alg": header["alg"] = "HS256"; break;
            case "kid": header["kid"] = "absent-key"; break;
            case "issuer": claims["iss"] = "https://other.test"; break;
            case "audience": claims["aud"] = "other-client"; break;
            case "extra-audience": claims["aud"] = new[] { "client-id", "other-client" }; break;
            case "missing-sub": claims.Remove("sub"); break;
            case "empty-sub": claims["sub"] = ""; break;
            case "missing-iat": claims.Remove("iat"); break;
            case "missing-exp": claims.Remove("exp"); break;
            case "future-iat": claims["iat"] = f.Now + 1; break;
            case "exp-before-iat": claims["exp"] = f.Now - 61; break;
            case "exact-expiry": claims["exp"] = f.Now; break;
            case "expired-within-skew": claims["exp"] = f.Now - 1; break;
            case "missing-nonce": claims.Remove("nonce"); break;
            case "wrong-nonce": claims["nonce"] = "wrong"; break;
            case "sub-mismatch": claims["sub"] = "different-account"; break;
        }
        var duplicate = defect.StartsWith("duplicate-", StringComparison.Ordinal) ? defect[10..] : null;
        f.Reply(kind, duplicate, defect == "signature");
        var result = await f.Redeem(TestContext.Current.CancellationToken);
        Assert.Equal(AdminCodeStatus.Invalid, result.Status);
        Assert.Null(result.Principal);
        Assert.Null(result.AccessToken);
        Assert.Null(result.IdToken);
        Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task IdRoleCannotAuthorize_AndConfiguredRoleUsesAccessOnly()
    {
        using var f = new Fixture();
        f.Id["role"] = "admin";
        f.Access["role"] = "student";
        f.Reply();
        Assert.Equal(AdminCodeStatus.Forbidden, (await f.Redeem(TestContext.Current.CancellationToken)).Status);
        f.Admin.RequiredRole = "curator";
        f.Access["role"] = "curator";
        f.Reply();
        Assert.Equal(AdminCodeStatus.Success, (await f.Redeem(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task UnknownKid_RefreshesOnlyReceivedTokens_NotCode()
    {
        using var f = new Fixture();
        f.Manager.HideKeyUntilRefresh = true;
        f.Reply();
        Assert.Equal(AdminCodeStatus.Success, (await f.Redeem(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, f.Manager.Refreshes);
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData("issuer")][InlineData("authorization-host")][InlineData("token-host")]
    [InlineData("jwks-host")][InlineData("http")][InlineData("authorization-query")]
    [InlineData("token-query")][InlineData("userinfo")][InlineData("fragment")]
    [InlineData("relative")][InlineData("different-port")]
    public async Task UntrustedMetadata_NeverPosts(string defect)
    {
        using var f = new Fixture();
        var c = f.Manager.Configuration;
        switch (defect)
        {
            case "issuer": c.Issuer += "/"; break;
            case "authorization-host": c.AuthorizationEndpoint = "https://evil.test/auth"; break;
            case "token-host": c.TokenEndpoint = "https://evil.test/token"; break;
            case "jwks-host": c.JwksUri = "https://evil.test/keys"; break;
            case "http": c.TokenEndpoint = "http://issuer.test/token"; break;
            case "authorization-query": c.AuthorizationEndpoint += "?x=1"; break;
            case "token-query": c.TokenEndpoint += "?x=1"; break;
            case "userinfo": c.TokenEndpoint = "https://user@issuer.test/token"; break;
            case "fragment": c.JwksUri += "#keys"; break;
            case "relative": c.AuthorizationEndpoint = "/authorize"; break;
            case "different-port": c.TokenEndpoint = "https://issuer.test:444/token"; break;
        }
        Assert.Equal(AdminCodeStatus.Unavailable, (await f.Redeem(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("client")][InlineData("secret")][InlineData("audience")][InlineData("scope-empty")]
    [InlineData("scope-offline")][InlineData("scope-missing-openid")][InlineData("scope-duplicate")]
    [InlineData("redirect-http")][InlineData("redirect-userinfo")][InlineData("redirect-fragment")]
    [InlineData("redirect-path")][InlineData("redirect-long")][InlineData("redirect-nonascii")]
    public async Task InvalidConfiguration_IsSafeAndDoesNotPost(string defect)
    {
        using var f = new Fixture();
        switch (defect)
        {
            case "client": f.CodeOptions.ClientId = ""; break;
            case "secret": f.CodeOptions.ClientSecret = ""; break;
            case "audience": f.Identity.Audience = "shared"; break;
            case "scope-empty": f.CodeOptions.Scope = ""; break;
            case "scope-offline": f.CodeOptions.Scope = "openid offline_access"; break;
            case "scope-missing-openid": f.CodeOptions.Scope = "profile"; break;
            case "scope-duplicate": f.CodeOptions.Scope = "openid openid"; break;
            case "redirect-http": f.CodeOptions.RedirectUri = "http://127.0.0.1/admin/auth/callback"; break;
            case "redirect-userinfo": f.CodeOptions.RedirectUri = "https://user@lexarbor.test/admin/auth/callback"; break;
            case "redirect-fragment": f.CodeOptions.RedirectUri += "#fragment"; break;
            case "redirect-path": f.CodeOptions.RedirectUri = "https://lexarbor.test/admin/auth/callback/"; break;
            case "redirect-long": f.CodeOptions.RedirectUri += "?x=" + new string('a', 500); break;
            case "redirect-nonascii": f.CodeOptions.RedirectUri += "?x=词"; break;
        }
        Assert.Equal(AdminCodeStatus.Unavailable, (await f.Redeem(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080/admin/auth/callback", true)]
    [InlineData("http://[::1]:5080/admin/auth/callback", true)]
    [InlineData("http://localhost:5080/admin/auth/callback", false)]
    [InlineData("http://remote.test/admin/auth/callback", false)]
    public async Task DevelopmentCallback_OnlyNumericLoopback(string uri, bool allowed)
    {
        using var f = new Fixture("Development");
        f.CodeOptions.RedirectUri = uri;
        f.Reply();
        Assert.Equal(allowed ? AdminCodeStatus.Success : AdminCodeStatus.Unavailable, (await f.Redeem(TestContext.Current.CancellationToken)).Status);
        if (allowed) Assert.Equal(uri, f.Form["redirect_uri"]);
    }

    [Theory]
    [InlineData("missing-access")][InlineData("missing-id")][InlineData("type")]
    [InlineData("expires")][InlineData("scope")][InlineData("oversize-token")]
    [InlineData("nonascii-token")][InlineData("malformed-token")]
    [InlineData("malformed-body")][InlineData("oversize-body")][InlineData("invalid-grant")]
    [InlineData("redirect307")][InlineData("redirect308")][InlineData("network")][InlineData("timeout")]
    public async Task ResponseAndTransportFailures_AreBoundedSinglePostAndSafe(string failure)
    {
        using var f = new Fixture();
        f.Reply();
        var response = f.Body;
        switch (failure)
        {
            case "missing-access": response.Remove("access_token"); break;
            case "missing-id": response.Remove("id_token"); break;
            case "type": response["token_type"] = "Basic"; break;
            case "expires": response["expires_in"] = 0; break;
            case "scope": response["scope"] = "openid offline_access"; break;
            case "oversize-token": response["access_token"] = new string('a', 8193); break;
            case "nonascii-token": response["id_token"] = "词"; break;
            case "malformed-token": response["id_token"] = "a.b.c"; break;
            case "invalid-grant": f.Status = HttpStatusCode.BadRequest; response = new() { ["error"] = "invalid_grant", ["error_description"] = Fixture.Code }; break;
            case "redirect307": f.Status = HttpStatusCode.TemporaryRedirect; break;
            case "redirect308": f.Status = HttpStatusCode.PermanentRedirect; break;
            case "network": f.Exception = new HttpRequestException(Fixture.Code); break;
            case "timeout": f.Exception = new TaskCanceledException(Fixture.Code); break;
        }
        f.RawBody = failure == "oversize-body" ? new string('a', 65537)
            : failure == "malformed-body" ? "{" + Fixture.Code : JsonSerializer.Serialize(response);
        var result = await f.Redeem(TestContext.Current.CancellationToken);
        Assert.NotEqual(AdminCodeStatus.Success, result.Status);
        if (failure == "invalid-grant") Assert.Equal(AdminCodeStatus.Invalid, result.Status);
        Assert.Null(result.Principal);
        Assert.Null(result.AccessToken);
        Assert.Null(result.IdToken);
        Assert.DoesNotContain(Fixture.Code, result.ToString()!);
        Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAndNeverCreatesIdentity()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Redeem(cancellation.Token));
        Assert.Equal(0, f.Posts);
        using var during = new CancellationTokenSource();
        f.DuringSend = () => during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Redeem(during.Token));
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData(307)][InlineData(308)]
    public async Task RegisteredRealBackchannel_DoesNotFollowRedirectOrLeakViaLoggers(int status)
    {
        using var factory = new VocabularyWebApplicationFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["AdminAuthentication:OidcCode:ClientSecret"] = ""
        }, environment: "Testing", includeAppCredentials: true);
        using var logs = new CapturedLogs();
        using var tracedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
        using var host = tracedFactory.CreateClient();
        using var scope = tracedFactory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AdminCodeExchange>());
        using var client = tracedFactory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(AdminCodeExchange.BackchannelName);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            using var stream = connection.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
            var wire = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Redirect\r\nLocation: {address}/evil\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(wire, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        using var response = await client.PostAsync(address + "/token", new StringContent("synthetic-body"), TestContext.Current.CancellationToken);
        await server;
        Assert.Equal(status, (int)response.StatusCode);
        Assert.False(listener.Pending());
        Assert.DoesNotContain(logs.Messages, line => line.Contains("synthetic-body", StringComparison.Ordinal) || line.Contains("/evil", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, line => line.Contains("LexarborCodeExchange", StringComparison.Ordinal));
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(categoryName, Messages);
        public void Dispose() { }
        private sealed class CapturedLogger(string category, System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(category + ": " + formatter(state, exception) + exception?.ToString());
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const string Code = "synthetic-sensitive-code-marker";
        public const string Verifier = "synthetic-verifier-marker-012345678901234567890123";
        public const string Nonce = "synthetic-nonce-marker-012345678901234567890123";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RSA _other = RSA.Create(2048);
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public OidcCodeOptions CodeOptions { get; } = new() { ClientId = "client-id", ClientSecret = "synthetic-secret-marker", RedirectUri = "https://lexarbor.test/admin/auth/callback?registered=1" };
        public IdentityServiceOptions Identity { get; } = new() { Issuer = "https://issuer.test", Audience = "client-id" };
        public AdminAuthenticationOptions Admin { get; } = new();
        public Dictionary<string, object> Access { get; }
        public Dictionary<string, object> Id { get; }
        public Dictionary<string, object> AccessHeader { get; } = new() { ["alg"] = "RS256", ["kid"] = "key-1", ["typ"] = "at+jwt" };
        public Dictionary<string, object> IdHeader { get; } = new() { ["alg"] = "RS256", ["kid"] = "key-1", ["typ"] = "JWT" };
        public Dictionary<string, object> Body { get; private set; } = new();
        public string AccessToken { get; private set; } = "";
        public string IdToken { get; private set; } = "";
        public string? RawBody { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Exception? Exception { get; set; }
        public Action? DuringSend { get; set; }
        public int Posts { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? ContentType { get; private set; }
        public Dictionary<string, string> Form { get; private set; } = new();
        public MetadataManager Manager { get; }
        private readonly AdminCodeExchange _service;
        private readonly HttpClient _http;
        public Fixture(string environment = "Production")
        {
            Access = Claims(); Access["role"] = "admin"; Access["name"] = "access-name";
            Id = Claims(); Id["nonce"] = Nonce; Id["name"] = "id-name";
            var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "key-1" });
            Manager = new MetadataManager(key);
            var options = new JwtBearerOptions { ConfigurationManager = Manager, RefreshOnIssuerKeyNotFound = true };
            var monitor = new Mock<IOptionsMonitor<JwtBearerOptions>>(); monitor.Setup(m => m.Get(It.IsAny<string>())).Returns(options);
            var env = new Mock<IHostEnvironment>(); env.SetupGet(e => e.EnvironmentName).Returns(environment);
            _http = new HttpClient(new Handler(async (request, cancellation) =>
            {
                Posts++; RequestUri = request.RequestUri!.AbsoluteUri; Authorization = request.Headers.Authorization?.ToString();
                ContentType = request.Content!.Headers.ContentType!.MediaType;
                Form = (await request.Content.ReadAsStringAsync(cancellation)).Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
                DuringSend?.Invoke(); cancellation.ThrowIfCancellationRequested();
                if (Exception is not null) throw Exception;
                return new HttpResponseMessage(Status) { Content = new StringContent(RawBody ?? JsonSerializer.Serialize(Body)) };
            }));
            var clients = new Mock<IHttpClientFactory>(); clients.Setup(c => c.CreateClient(AdminCodeExchange.BackchannelName)).Returns(_http);
            _service = new AdminCodeExchange(clients.Object, Options.Create(CodeOptions), Options.Create(Identity), Options.Create(Admin), monitor.Object, env.Object, new Clock(DateTimeOffset.FromUnixTimeSeconds(Now)));
        }
        private Dictionary<string, object> Claims() => new() { ["iss"] = "https://issuer.test", ["aud"] = "client-id", ["sub"] = "account-42", ["iat"] = Now - 60, ["exp"] = Now + 300 };
        public void Reply(string? badKind = null, string? duplicate = null, bool badSignature = false)
        {
            AccessToken = Token(AccessHeader, Access, badKind == "access" ? duplicate : null, badKind == "access" && badSignature);
            IdToken = Token(IdHeader, Id, badKind == "id" ? duplicate : null, badKind == "id" && badSignature);
            Body = new() { ["access_token"] = AccessToken, ["id_token"] = IdToken, ["token_type"] = "Bearer", ["expires_in"] = 900, ["scope"] = "openid profile", ["refresh_token"] = "ignored-synthetic-refresh" };
        }
        private string Token(Dictionary<string, object> header, Dictionary<string, object> claims, string? duplicate, bool badSignature)
        {
            var json = JsonSerializer.Serialize(claims);
            if (duplicate is not null) json = json[..^1] + $",\"{duplicate}\":" + JsonSerializer.Serialize(claims[duplicate]) + "}";
            var signing = Base64UrlEncoder.Encode(JsonSerializer.Serialize(header)) + "." + Base64UrlEncoder.Encode(json);
            return signing + "." + Base64UrlEncoder.Encode((badSignature ? _other : _rsa).SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        public Task<AdminCodeResult> Redeem(CancellationToken cancellation = default) => _service.RedeemAsync(Code, Verifier, Nonce, cancellation);
        public void Dispose() { _http.Dispose(); _rsa.Dispose(); _other.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class MetadataManager(SecurityKey key) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public OpenIdConnectConfiguration Configuration { get; } = new() { Issuer = "https://issuer.test", AuthorizationEndpoint = "https://issuer.test/discovered/authorize", TokenEndpoint = "https://issuer.test/discovered/token", JwksUri = "https://issuer.test/discovered/keys" };
        public bool HideKeyUntilRefresh { get; set; }
        public int Refreshes { get; private set; }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            Configuration.SigningKeys.Clear();
            if (!HideKeyUntilRefresh || Refreshes > 0) Configuration.SigningKeys.Add(key);
            return Task.FromResult(Configuration);
        }
        public void RequestRefresh() => Refreshes++;
    }
}
