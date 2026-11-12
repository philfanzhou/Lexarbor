using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The standard RP-initiated logout on the framework handler: the local session always ends
/// first with its audit row, only the Discovery end-session URL — carrying the server-held ID
/// token as <c>id_token_hint</c>, the registered post-logout URI and the protected state —
/// reaches the envelope, and the fixed return route answers only with in-site redirects. The
/// fixture signs in through the real hosted code flow so the persisted ID token used as the
/// logout hint is a genuine RS256 JWT. The logout endpoint carries the antiforgery boundary:
/// every logout request presents the token pair from <c>GET /admin/auth/csrf</c>.
/// </summary>
public class AdminHostedLogoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignedInLogout_ReturnsStandardEndSessionUrlAndCompletesReturnTrip()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var logoutUrl = await LogoutUrlOf(response);
        Assert.NotNull(logoutUrl);
        // The standard RP-initiated logout: the Discovery end-session endpoint, the
        // server-held ID token as the hint, the registered post-logout URI, and the
        // protected state that lands the browser back on the SPA result. Exactly like the
        // prepared logout it replaces, the URL is presented — never followed server-side.
        Assert.Equal(SignaCoreAuthorityStub.EndSessionEndpoint, new Uri(logoutUrl).GetLeftPart(UriPartial.Path));
        var parameters = QueryHelpers.ParseQuery(new Uri(logoutUrl).Query);
        Assert.Equal(f.LastId, parameters["id_token_hint"].ToString());
        Assert.Equal(SignaCoreAuthorityStub.PostLogoutRedirect, parameters["post_logout_redirect_uri"].ToString());
        var state = parameters["state"].ToString();
        Assert.False(string.IsNullOrEmpty(state));
        Assert.Single(parameters.Keys, key => key == "state");
        Assert.DoesNotContain("x-client-SKU", logoutUrl, StringComparison.Ordinal);
        Assert.DoesNotContain(f.LastAccess, logoutUrl);
        Assert.DoesNotContain(SignaCoreAuthorityStub.Secret, logoutUrl);
        // The session cookie is deleted; the standard flow needs no extra correlation
        // cookie — the protected state round-trips through the authority's own redirect.
        AssertSessionCookieDeleted(response);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.Contains("logout-return", StringComparison.Ordinal));
        // The revoked session is gone server-side and for the presented handle.
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        using var scope = f.Host.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
        // The browser completes upstream and returns with the echoed state.
        using var back = await f.Return("?state=" + Uri.EscapeDataString(state));
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/#/login?reason=logged_out", back.Headers.Location!.OriginalString);
        ReturnSafety(back);
        // Declared difference of the standard flow: the return's state is a protected,
        // server-independent value, so a replay lands on the same SPA result instead of
        // the replaced slice's one-time failure.
        using var replay = await f.Return("?state=" + Uri.EscapeDataString(state));
        Assert.Equal("/#/login?reason=logged_out", replay.Headers.Location!.OriginalString);
        ReturnSafety(replay);
        f.AssertNotLogged(state, f.LastId, f.LastAccess, SignaCoreAuthorityStub.Secret);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task WithoutConfiguredReturnRedirect_LogoutUrlCarriesHintOnly()
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = "" });
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        var logoutUrl = await LogoutUrlOf(response);
        Assert.NotNull(logoutUrl);
        var parameters = QueryHelpers.ParseQuery(new Uri(logoutUrl!).Query);
        // Without a registered return redirect neither the post_logout_redirect_uri nor a
        // state travels upstream, so the fixed return route cannot complete: the failed
        // redirect stands, exactly like the replaced prepared logout.
        Assert.Equal(f.LastId, parameters["id_token_hint"].ToString());
        Assert.False(parameters.ContainsKey("post_logout_redirect_uri"));
        Assert.False(parameters.ContainsKey("state"));
        using var back = await f.Client.GetAsync("/admin/auth/logout/return", Ct);
        Assert.Equal("/#/login?reason=logout_failed", back.Headers.Location!.OriginalString);
        ReturnSafety(back);
    }

    [Theory]
    [InlineData("https://lexarbor.test/admin/auth/logout/return?state=pre")]
    [InlineData("https://lexarbor.test/admin/auth/logout/return?x=1&x=2")]
    [InlineData("https://lexarbor.test/admin/auth/logout/return#f")]
    [InlineData("https://user@lexarbor.test/admin/auth/logout/return")]
    [InlineData("/admin/auth/logout/return")]
    [InlineData("https://lexarbor.test/ADMIN/AUTH/LOGOUT/RETURN")]
    public async Task ConfiguredButIllegalReturnRedirect_FailsStartup(string configured)
    {
        // A registered static query or any other illegal shape is a startup failure:
        // the validator accepts no query on the redirect URIs.
        var config = new Dictionary<string, string?>
        { ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = configured };
        using var f = new Fixture(config, expectStartupFailure: true);
        Assert.True(f.StartupFailed);
    }

    [Fact]
    public async Task MissingEndSessionEndpoint_LeavesLocalRevocationOnly()
    {
        using var f = new Fixture();
        f.Authority.EndSessionUnavailable = true;
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        // An authority that publishes no end-session endpoint degrades the logout to the
        // fixed local-only result; the local revocation stands.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        AssertSessionCookieDeleted(response);
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        await LocalOnly(await f.Logout(cookie));
        f.AssertSafeLogs();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("legacy")]
    [InlineData("bad-handle")]
    public async Task SessionlessLogouts_AreIdempotentLocalOnly(string kind)
    {
        using var f = new Fixture();
        var cookie = kind switch
        {
            "legacy" => VocabularyWebApplicationFactory.CookieName + "=" + f.Authority.Token("JWT", f.Authority.AccessClaims()),
            "bad-handle" => AdminSessionCookie.Name + "=bad",
            _ => ""
        };
        using var response = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        AssertSessionCookieDeleted(response);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task SessionWithoutIdToken_OrExpired_EndsLocally()
    {
        using var f = new Fixture();
        var tokenless = await f.Seed(idToken: null);
        using var first = await f.Logout(f.Cookie(tokenless));
        // A session stored without an ID-token hint cannot start the standard flow: the
        // local revocation stands and the answer stays local-only.
        Assert.Equal("{\"success\":true}", await first.Content.ReadAsStringAsync(Ct));
        Assert.Null(await LogoutUrlOf(first));
        var expired = await f.Seed();
        f.Clock.Now += TimeSpan.FromMinutes(16);
        using var second = await f.Logout(f.Cookie(expired));
        Assert.Equal("{\"success\":true}", await second.Content.ReadAsStringAsync(Ct));
        using var gone = await f.Send("/admin/auth/session", f.Cookie(expired));
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
    }

    [Fact]
    public async Task RepeatedLogout_NeverReportsASecondUpstreamRedirect()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var first = await f.Logout(cookie);
        Assert.NotNull(await LogoutUrlOf(first));
        // The second logout of the same handle finds no live session to revoke: the fixed
        // local-only result, never a second end-session URL.
        using var second = await f.Logout(cookie);
        Assert.Equal("{\"success\":true}", await second.Content.ReadAsStringAsync(Ct));
        using var third = await f.Logout();
        Assert.Equal("{\"success\":true}", await third.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentLogouts_TakeAtMostOneLiveRevocationAndOneRedirect()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        // Both requests pass the antiforgery boundary, but only the one whose revocation
        // actually ends the live session — decided atomically in the storage transaction —
        // may report the upstream end-session URL; the loser answers local-only.
        var responses = await Task.WhenAll(f.LogoutAsync(cookie, Ct), f.LogoutAsync(cookie, Ct));
        var prepared = responses.Count(response => LogoutUrlOf(response).GetAwaiter().GetResult() is not null);
        Assert.Equal(1, prepared);
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task LogoutWithoutAntiforgeryToken_IsRejectedWithoutRevocation()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie, csrf: false);
        // The fixed antiforgery rejection: 400, no session touched.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("csrf_rejected", await response.Content.ReadAsStringAsync(Ct));
        // Only the sign-in's single redemption happened; the rejected logout never dials out.
        Assert.Equal(1, f.TokenPosts);
        using var session = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task BearerSelectedLogout_RevokesPresentedSessionButNeverFabricatesOne()
    {
        using var f = new Fixture();
        var bearer = f.Authority.Token("at+jwt", f.Authority.AccessClaims());
        var (token, antiforgery) = AdminTestAntiforgery.Get(f.Base);
        var plain = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        plain.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        plain.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        plain.Headers.Add("Cookie", antiforgery);
        using var withoutCookie = await f.Client.SendAsync(plain, Ct);
        Assert.Equal(HttpStatusCode.OK, withoutCookie.StatusCode);
        Assert.Equal("{\"success\":true}", await withoutCookie.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", withoutCookie.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.TokenPosts);
        var cookie = await f.SignIn();
        var both = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        both.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        both.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        both.Headers.Add("Cookie", cookie + "; " + antiforgery);
        using var withCookie = await f.Client.SendAsync(both, Ct);
        // The presented session is revoked and the standard end-session URL is prepared
        // from its server-held ID token; the Bearer credential itself never fabricates one.
        Assert.NotNull(await LogoutUrlOf(withCookie));
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("marker")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("no-query")]
    public async Task InvalidReturnStates_AlwaysEndInTheSameFixedFailedRedirect(string defect)
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var logout = await f.Logout(cookie);
        var state = QueryHelpers.ParseQuery(new Uri((await LogoutUrlOf(logout))!).Query)["state"].ToString();
        var query = defect switch
        {
            "missing" => "",
            "marker" => "?state=sensitive-state-marker",
            "empty" => "?state=",
            "duplicate" => "?state=" + state + "&state=" + state,
            "unknown" => "?state=" + WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            _ => ""
        };
        using var response = await f.Return(query);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/#/login?reason=logout_failed", response.Headers.Location!.OriginalString);
        ReturnSafety(response);
        Assert.True(!response.Headers.TryGetValues("Set-Cookie", out var cookies) || !cookies.Any());
        Assert.DoesNotContain("sensitive-state-marker", await response.Content.ReadAsStringAsync(Ct));
        f.AssertNotLogged(state, "sensitive-state-marker");
        f.AssertSafeLogs();
    }

    [Theory]
    [InlineData("/admin/auth/logout")]
    [InlineData("/admin/auth/logout/")]
    [InlineData("/ADMIN/AUTH/LOGOUT")]
    [InlineData("/Admin/Auth/Logout/")]
    public async Task LogoutRouteForms_CarryNoStoreOnEveryAcceptedForm(string path)
    {
        using var f = new Fixture();
        using var response = await f.Logout(null, path: path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.TokenPosts);
    }

    [Theory]
    [InlineData("/admin/auth/logout/return")]
    [InlineData("/ADMIN/AUTH/LOGOUT/RETURN")]
    public async Task ReturnRouteForms_CarrySafetyHeadersOnEveryAcceptedForm(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/#/login?reason=logout_failed", response.Headers.Location!.OriginalString);
        ReturnSafety(response);
    }

    // Declared difference of the framework handler: the sign-out callback is intercepted by
    // its exact configured path, not by the router's trailing-slash-insensitive literal
    // matching. The non-canonical form stays on the authenticated admin catch-all with the
    // same fixed 401 and security-header baseline as any unknown /admin route.
    [Theory]
    [InlineData("/admin/auth/logout/return/")]
    [InlineData("/Admin/Auth/Logout/Return/")]
    public async Task TrailingSlashReturnForms_KeepAdminCatchallBehavior(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Authentication is required.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    // Restricted normalization must not claim paths routing itself rejects: a double
    // trailing slash or an extra segment stays on the authenticated admin catchall.
    [Theory]
    [InlineData("/admin/auth/logout/return//")]
    [InlineData("/admin/auth/logout/return/extra")]
    public async Task NonRouteReturnForms_KeepAdminCatchallBehavior(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Authentication is required.", await response.Content.ReadAsStringAsync(Ct));
        // The catch-all is part of the /admin surface, so its answers carry the
        // shared security response-header baseline.
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.TokenPosts);
    }

    [Fact]
    public async Task DefaultRegisteredLogging_DoesNotProjectLogoutMaterial()
    {
        using var f = new Fixture(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
        });
        var cookie = await f.SignIn();
        using var logout = await f.Logout(cookie);
        Assert.NotNull(await LogoutUrlOf(logout));
        var state = QueryHelpers.ParseQuery(new Uri((await LogoutUrlOf(logout))!).Query)["state"].ToString();
        using var back = await f.Return("?state=" + Uri.EscapeDataString(state));
        Assert.Equal("/#/login?reason=logged_out", back.Headers.Location!.OriginalString);
        f.AssertNotLogged(state, SignaCoreAuthorityStub.Secret, f.LastId, f.LastAccess);
    }

    private static async Task<JsonElement> LocalOnly(HttpResponseMessage response)
    {
        // The fixed local-only outcome: the local session was revoked (or there was none)
        // and no upstream redirect could be handed to the browser.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<string?> LogoutUrlOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("logoutUrl", out var url)
            && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
    }

    private static void AssertSessionCookieDeleted(HttpResponseMessage response)
    {
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, c => c.StartsWith(AdminSessionCookie.Name + "=;", StringComparison.Ordinal));
    }

    private static void ReturnSafety(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    private sealed record Transaction(string State, string Cookie);

    private sealed class Fixture : IDisposable
    {
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        // Unassigned only on the expectStartupFailure path, where tests assert
        // StartupFailed and never send requests.
        public HttpClient Client { get; } = null!;
        public SignaCoreAuthorityStub Authority { get; }
        public Clock Clock { get; } = new();
        public Logs Logs { get; } = new();
        public bool StartupFailed { get; private set; }
        private (string Token, string Cookie)? _csrfPair;

        public Fixture(Dictionary<string, string?>? extra = null, string provider = "OidcCode", bool expectStartupFailure = false)
        {
            var config = new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = SignaCoreAuthorityStub.Issuer,
                ["IdentityService:Issuer"] = SignaCoreAuthorityStub.Issuer,
                ["IdentityService:Audience"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = SignaCoreAuthorityStub.Secret,
                ["AdminAuthentication:OidcCode:RedirectUri"] = SignaCoreAuthorityStub.Redirect,
                ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = SignaCoreAuthorityStub.PostLogoutRedirect,
                ["AdminAuthentication:OidcCode:Scope"] = "openid profile",
                ["RateLimits:AdminLogin:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace"
            };
            if (extra is not null) foreach (var pair in extra) config[pair.Key] = pair.Value;
            Authority = new SignaCoreAuthorityStub(Clock);
            Base = new VocabularyWebApplicationFactory("Testing", true, provider, config);
            Host = Base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.Authority = null; options.MetadataAddress = null!; options.ConfigurationManager = null!;
                        options.TokenValidationParameters.IssuerSigningKey = Authority.SigningKey;
                    });
                    services.AddHttpClient(AdminAuthenticationSource.OidcHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => Authority);
                });
            });
            try
            {
                Client = Host.CreateClient(new() { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
                if (expectStartupFailure)
                {
                    using var failing = Client.GetAsync("/health/live", Ct).GetAwaiter().GetResult();
                    Assert.Fail("An illegal post-logout redirect configuration must fail host startup.");
                }
            }
            catch (OptionsValidationException)
            {
                StartupFailed = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // The test host surfaces a failed startup (host construction or start)
                // as its own disposal.
                StartupFailed = true;
            }
        }

        public async Task<string> SignIn()
        {
            var t = await Start();
            using var callback = await Send(Query(t), t.Cookie);
            Assert.Equal("/#/books", callback.Headers.Location!.OriginalString);
            return callback.Headers.GetValues("Set-Cookie")
                .Single(s => s.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        }

        public async Task<Transaction> Start()
        {
            using var response = await Client.GetAsync("/admin/auth/start?returnUrl=/books", Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            var state = parameters["state"].ToString();
            Authority.Challenges[parameters["code_challenge"].ToString()] = parameters["nonce"].ToString();
            var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
            return new(state, cookie);
        }

        public string Query(Transaction t) => "/admin/auth/callback?state=" + t.State
            + "&iss=" + Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer) + "&code=" + SignaCoreAuthorityStub.Code;

        public Task<HttpResponseMessage> Logout(string? cookie = null, bool csrf = true, string path = "/admin/auth/logout") =>
            Send(path, cookie, "POST", null, csrf);

        public Task<HttpResponseMessage> LogoutAsync(string? cookie, CancellationToken cancellationToken)
        {
            if (_csrfPair is null) _csrfPair = FetchCsrf();
            var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
            request.Headers.Add("Cookie", JoinCookies(cookie, _csrfPair.Value.Cookie));
            request.Headers.Add(AdminTestAntiforgery.HeaderName, _csrfPair.Value.Token);
            return Client.SendAsync(request, cancellationToken);
        }

        public Task<HttpResponseMessage> Return(string query, string cookie = "") =>
            Send("/admin/auth/logout/return" + query, cookie);

        public Task<HttpResponseMessage> Send(string path, string? cookie = null, string method = "GET", HttpContent? body = null, bool csrf = false)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
            var cookies = new List<string>();
            if (!string.IsNullOrEmpty(cookie)) cookies.Add(cookie);
            if (csrf)
            {
                _csrfPair ??= FetchCsrf();
                request.Headers.Add(AdminTestAntiforgery.HeaderName, _csrfPair.Value.Token);
                cookies.Add(_csrfPair.Value.Cookie);
            }
            if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies));
            return Client.SendAsync(request, Ct);
        }

        private static string JoinCookies(string? first, string second) =>
            string.IsNullOrEmpty(first) ? second : first + "; " + second;

        private (string Token, string Cookie) FetchCsrf()
        {
            using var response = Client.GetAsync("/admin/auth/csrf", Ct).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(response.Content.ReadAsStream(Ct)).RootElement.GetProperty("token").GetString()!;
            return (token, response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        }

        public async Task<string> Seed(string? idToken = "synthetic-old-id-marker")
        {
            using var scope = Host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
            { AccessToken = "synthetic-old-access-marker", IdToken = idToken, Issuer = SignaCoreAuthorityStub.Issuer, Subject = "old-sub", DisplayName = "old-user", Roles = ["admin"], AccessTokenExpiresAt = Clock.Now.AddMinutes(15) }, Ct);
        }

        /// <summary>The session-cookie value a signed-in browser would hold for a stored handle.</summary>
        public string Cookie(string handle) => AdminSessionTestCookie.For(Host.Services, handle);

        public int TokenPosts => Authority.TokenPosts;
        public string LastAccess => Authority.LastAccess;
        public string LastId => Authority.LastId;

        public void AssertNotLogged(params string[] markers)
        {
            foreach (var marker in markers)
                if (marker.Length > 0) Assert.DoesNotContain(Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
        }

        public void AssertSafeLogs() => AssertNotLogged(SignaCoreAuthorityStub.Code, SignaCoreAuthorityStub.Secret, LastAccess, LastId, SignaCoreAuthorityStub.Handle,
            Authority.TokenForm.GetValueOrDefault("code_verifier", ""), "sensitive-upstream-marker", "sensitive-handle-marker",
            "sensitive-state-marker", "synthetic-old-access-marker", "synthetic-old-id-marker",
            "synthetic-first-id-marker", "synthetic-second-id-marker",
            "synthetic-upstream-network-marker", "synthetic-upstream-timeout-marker");

        public void Dispose() { Client?.Dispose(); Host.Dispose(); Base.Dispose(); Authority.Dispose(); }
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

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
