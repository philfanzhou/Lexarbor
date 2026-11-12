using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The plain-HTTP redirect topology of the cookie profiles: http and https redirect URIs are
/// equal inputs in every environment — no environment privilege, loopback exception or origin
/// allowlist — and the whole cookie set (names and attributes) derives from the redirect URI's
/// scheme. These tests pin the restored private-network plain-HTTP browser login (start,
/// callback, session, the antiforgery write model, logout) against a non-loopback synthetic
/// http authority and http redirect URIs, and the de-prefixed session cookie name that the
/// profile keeps. The HTTPS profile keeps its regression baseline in the loopback-issuer /
/// https-redirect fixtures of the other hosted-login suites.
/// </summary>
public class AdminHostedLoginPlainHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Issuer = "http://identity.test";
    private const string Redirect = "http://lexarbor.test/admin/auth/callback";
    private const string PostLogoutRedirect = "http://lexarbor.test/admin/auth/logout/return";

    [Fact]
    public void PlainHttpRedirect_DerivesTheDePrefixedSessionCookieName()
    {
        using var f = new Fixture();
        var cookie = f.Host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuthenticationSource.SessionScheme);
        // The plain-HTTP profile drops the __Host- prefix (a prefixed session name demands
        // the Secure attribute a plain-HTTP deployment cannot set); the https profile and
        // the optional-login blank keep the historical byte-for-byte name.
        Assert.Equal(AdminSessionCookie.PlainHttpName, cookie.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.None, cookie.Cookie.SecurePolicy);
        Assert.Equal(AdminSessionCookie.Name, AdminSessionCookie.ForRedirectUri("https://lexarbor.test/admin/auth/callback"));
        Assert.Equal(AdminSessionCookie.Name, AdminSessionCookie.ForRedirectUri(null));
        Assert.Equal(AdminSessionCookie.Name, AdminSessionCookie.ForRedirectUri(""));
        Assert.Equal(AdminSessionCookie.PlainHttpName, AdminSessionCookie.ForRedirectUri("http://192.168.50.10:5008/admin/auth/callback"));
        Assert.Equal(AdminSessionCookie.PlainHttpName, AdminSessionCookie.ForRedirectUri("http://identity.test:8443/admin/auth/callback"));
    }

    [Fact]
    public async Task PlainHttpTopology_CarriesTheFullBrowserChainWithNonSecureDerivedCookies()
    {
        using var f = new Fixture();

        // start: the framework carrier cookies carry every attribute of the plain-HTTP
        // profile — no Secure, no prefix anywhere.
        using var start = await f.Client.GetAsync("/admin/auth/start", Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var parameters = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        f.Authority.Challenges[parameters["code_challenge"].ToString()] = parameters["nonce"].ToString();
        var state = parameters["state"].ToString();
        Assert.Equal(2, start.Headers.GetValues("Set-Cookie").Count());
        AssertNoSecureAndNoPrefixes(start);

        // callback: the session cookie takes the de-prefixed name without Secure and
        // the one-time carrier cookies are deleted beside it.
        var bindingCookie = string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        var callbackRequest = new HttpRequestMessage(HttpMethod.Get,
            "/admin/auth/callback?state=" + state + "&iss=" + Uri.EscapeDataString(Issuer)
            + "&code=" + SignaCoreAuthorityStub.Code);
        callbackRequest.Headers.Add("Cookie", bindingCookie);
        using var callback = await f.Client.SendAsync(callbackRequest, Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        AssertNoSecureAndNoPrefixes(callback);
        var sessionCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(AdminSessionCookie.PlainHttpName + "=", StringComparison.Ordinal));
        var handle = sessionCookie.Split(';')[0];
        Assert.Contains("; path=/", sessionCookie, StringComparison.OrdinalIgnoreCase);

        // the antiforgery pair of the write model is issued without Secure too
        var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/csrf");
        csrfRequest.Headers.Add("Cookie", handle);
        using var csrf = await f.Client.SendAsync(csrfRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        var token = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var antiforgeryCookie = csrf.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.DoesNotContain("secure", csrf.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);

        // session reads through the de-prefixed cookie
        var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        sessionRequest.Headers.Add("Cookie", handle);
        using var session = await f.Client.SendAsync(sessionRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Contains("access-user", await session.Content.ReadAsStringAsync(Ct));

        // a token-carrying write of the management API succeeds
        var writeRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/vocabulary-books")
        {
            Content = JsonContent.Create(new { bookName = "Plain-HTTP book", status = true })
        };
        writeRequest.Headers.Add("Cookie", handle + "; " + antiforgeryCookie);
        writeRequest.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        using var write = await f.Client.SendAsync(writeRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        // logout revokes, deletes the de-prefixed session cookie without Secure and
        // hands the browser the standard http end-session URL with its protected state
        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        logoutRequest.Headers.Add("Cookie", handle + "; " + antiforgeryCookie);
        logoutRequest.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        using var logout = await f.Client.SendAsync(logoutRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using var logoutBody = JsonDocument.Parse(await logout.Content.ReadAsStringAsync(Ct));
        var logoutUrl = logoutBody.RootElement.GetProperty("data").GetProperty("logoutUrl").GetString()!;
        Assert.StartsWith(Issuer + "/oauth2/logout/end?id_token_hint=", logoutUrl, StringComparison.Ordinal);
        var logoutQuery = QueryHelpers.ParseQuery(new Uri(logoutUrl).Query);
        Assert.Equal(PostLogoutRedirect, logoutQuery["post_logout_redirect_uri"].ToString());
        var logoutState = logoutQuery["state"].ToString();
        AssertNoSecureAndNoPrefixes(logout);

        // the completed return trip lands on the fixed signed-out SPA result
        var returnRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/logout/return?state=" + Uri.EscapeDataString(logoutState));
        using var returned = await f.Client.SendAsync(returnRequest, Ct);
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        Assert.Equal("/#/login?reason=logged_out", returned.Headers.Location!.OriginalString);

        // the revoked handle authenticates nothing anymore
        var goneRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        goneRequest.Headers.Add("Cookie", handle);
        using var gone = await f.Client.SendAsync(goneRequest, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
    }

    private static void AssertNoSecureAndNoPrefixes(HttpResponseMessage response)
    {
        foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
        {
            Assert.False(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase),
                "A plain-HTTP-profile cookie must not carry Secure: " + cookie.Split('=')[0]);
            Assert.False(cookie.StartsWith("__Host-", StringComparison.Ordinal),
                "A plain-HTTP-profile cookie must not carry the __Host- prefix: " + cookie.Split('=')[0]);
            Assert.False(cookie.StartsWith("__Secure-", StringComparison.Ordinal),
                "A plain-HTTP-profile cookie must not carry the __Secure- prefix: " + cookie.Split('=')[0]);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client { get; }
        public SignaCoreAuthorityStub Authority { get; } = new(TimeProvider.System, Issuer);

        public Fixture()
        {
            var config = new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = Issuer,
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = SignaCoreAuthorityStub.Secret,
                ["AdminAuthentication:OidcCode:RedirectUri"] = Redirect,
                ["AdminAuthentication:OidcCode:Scope"] = "openid profile",
                ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = PostLogoutRedirect,
                ["RateLimits:AdminLogin:Enabled"] = "false"
            };
            Base = new VocabularyWebApplicationFactory("Testing", true, "OidcCode", config);
            Host = Base.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null; options.MetadataAddress = null!; options.ConfigurationManager = null!;
                    options.TokenValidationParameters.IssuerSigningKey = Authority.SigningKey;
                });
                services.AddHttpClient(AdminAuthenticationSource.OidcHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Authority);
            }));
            Client = Host.CreateClient(new()
            { BaseAddress = new Uri("http://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
            Base.Dispose();
            Authority.Dispose();
        }
    }
}
