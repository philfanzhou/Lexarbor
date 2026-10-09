using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The plain-HTTP redirect topology of the official SignaCore client 0.1.16 (#209):
/// http and https redirect URIs are equal inputs in every environment — no
/// environment privilege, loopback exception or origin allowlist — and the whole
/// cookie set (names and attributes) derives from the redirect URI's scheme. These
/// tests pin the restored private-network plain-HTTP browser login (start, callback,
/// session, the antiforgery write model, logout) against a non-loopback synthetic
/// http authority and http redirect URIs, and the de-prefixed session cookie name
/// that keeps the package's startup validator satisfied. The HTTPS profile keeps its
/// regression baseline in the loopback-issuer/https-redirect fixtures of the other
/// hosted-login suites.
/// </summary>
public class AdminHostedLoginPlainHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Issuer = "http://identity.test";
    private const string Redirect = "http://lexarbor.test/admin/auth/callback";
    private const string PostLogoutRedirect = "http://lexarbor.test/admin/auth/logout/return";
    private const string LogoutReturnCookieName = "Lexarbor.AdminSession-logout-return";

    [Fact]
    public void PlainHttpRedirect_DerivesTheDePrefixedSessionCookieName()
    {
        using var f = new Fixture();
        var options = f.Host.Services.GetRequiredService<IOptions<SignaCoreHostedLoginOptions>>().Value;
        Assert.Equal(Redirect, options.RedirectUri);
        // The plain-HTTP profile drops the __Host- prefix (the package's validator
        // refuses a prefixed session name against an http redirect URI); the https
        // profile and the optional-login blank keep the historical byte-for-byte name.
        Assert.Equal(AdminSessionCookie.PlainHttpName, options.SessionCookieName);
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

        // start: the binding cookie carries the de-prefixed derived name and every
        // attribute of the plain-HTTP profile — no Secure, no prefix anywhere.
        using var start = await f.Client.GetAsync("/admin/auth/start", Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var parameters = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        f.Authority.Challenges[parameters["code_challenge"].ToString()] = parameters["nonce"].ToString();
        var state = parameters["state"].ToString();
        var bindingCookie = start.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(AdminSessionCookie.PlainHttpName + "-login-binding.", bindingCookie, StringComparison.Ordinal);
        AssertNoSecureAndNoPrefixes(start);

        // callback: the session cookie takes the de-prefixed name without Secure and
        // the one-time binding cookie is deleted beside it.
        var callbackRequest = new HttpRequestMessage(HttpMethod.Get,
            "/admin/auth/callback?state=" + state + "&iss=" + Uri.EscapeDataString(Issuer)
            + "&code=" + SignaCoreAuthorityStub.Code);
        callbackRequest.Headers.Add("Cookie", bindingCookie.Split(';')[0]);
        using var callback = await f.Client.SendAsync(callbackRequest, Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        AssertNoSecureAndNoPrefixes(callback);
        var sessionCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(AdminSessionCookie.PlainHttpName + "=", StringComparison.Ordinal));
        var handle = sessionCookie.Split(';')[0];
        Assert.Contains("; path=/", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(AdminSessionCookie.PlainHttpName + "-login-binding." + state + "=;", StringComparison.Ordinal));

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
        // hands the browser the http one-time upstream URI
        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        logoutRequest.Headers.Add("Cookie", handle + "; " + antiforgeryCookie);
        logoutRequest.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        using var logout = await f.Client.SendAsync(logoutRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains(Issuer + "/oauth2/logout?logout_handle=", await logout.Content.ReadAsStringAsync(Ct));
        var logoutCookies = logout.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(logoutCookies, c => c.StartsWith(AdminSessionCookie.PlainHttpName + "=;", StringComparison.Ordinal));
        Assert.Contains(logoutCookies, c => c.StartsWith(VocabularyWebApplicationFactory.CookieName + "=;", StringComparison.Ordinal));
        Assert.Contains(logoutCookies, c => c.StartsWith(LogoutReturnCookieName + "=", StringComparison.Ordinal));
        Assert.DoesNotContain(logoutCookies, c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));

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
                services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
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
