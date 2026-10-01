using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
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
/// Code-mode prepared logout: the local session always ends first, only a verified
/// one-time upstream logout URI reaches the envelope, and the fixed return route
/// answers only with in-site redirects. The fixture signs in through the real hosted
/// code flow so the persisted ID token used as the logout hint is a genuine RS256 JWT.
/// </summary>
public class AdminHostedLogoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string LogoutCookiePrefix = "__Host-Lexarbor.Logout.";
    private static string ExpectedLogoutUrl => Fixture.Issuer + "/oauth2/logout?logout_handle=" + Fixture.Handle;

    [Fact]
    public async Task SignedInLogout_ReturnsVerifiedLogoutUrlAndCompletesReturnTrip()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal("{\"success\":true,\"data\":{\"logoutUrl\":\"" + ExpectedLogoutUrl + "\"}}", body);
        Assert.DoesNotContain(f.LastId, body);
        Assert.DoesNotContain(f.LastAccess, body);
        Assert.DoesNotContain(Fixture.Secret, body);
        // Exactly one preparation, with the complete confidential form and the
        // genuine signed-in ID token as the hint; never a Basic header.
        Assert.Equal(1, f.LogoutPosts);
        Assert.Equal(5, f.LogoutForm.Count);
        Assert.Equal("client-id", f.LogoutForm["client_id"]);
        Assert.Equal(Fixture.Secret, f.LogoutForm["client_secret"]);
        Assert.Equal(f.LastId, f.LogoutForm["id_token_hint"]);
        Assert.Equal(Fixture.PostLogoutRedirect, f.LogoutForm["post_logout_redirect_uri"]);
        Assert.True(PendingAdminLogoutStore.Canonical(f.LogoutForm["state"]));
        // Both session cookies are deleted; the transaction cookie is HttpOnly,
        // Secure, Lax, path-bound, five-minute and carries only the binding.
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Equal(3, cookies.Length);
        Assert.Contains(cookies, c => c.StartsWith(AdminSessionCookie.Name + "=;", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith(VocabularyWebApplicationFactory.CookieName + "=;", StringComparison.Ordinal));
        var (name, binding, raw) = LogoutTransactionCookie(response);
        Assert.Equal(LogoutCookiePrefix + f.LogoutForm["state"], name);
        Assert.Contains("httponly", raw); Assert.Contains("secure", raw); Assert.Contains("samesite=lax", raw);
        Assert.Contains("path=/", raw); Assert.Contains("max-age=300", raw); Assert.DoesNotContain("domain=", raw);
        // The revoked session is gone server-side and for the presented handle.
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        using var scope = f.Host.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
        // The browser completes upstream and returns with the echoed state.
        using var back = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/#/login?reason=logged_out", back.Headers.Location!.OriginalString);
        ReturnSafety(back);
        var backCookies = Assert.Single(back.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(name + "=;", backCookies, StringComparison.Ordinal);
        // The consumed state never works twice, and nothing else is deleted.
        using var replay = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal("/#/login?reason=logout_failed", replay.Headers.Location!.OriginalString);
        ReturnSafety(replay);
        Assert.False(replay.Headers.Contains("Set-Cookie"));
        f.AssertNotLogged(f.LogoutForm["state"], binding, Fixture.Handle);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task UpstreamAnswer_IsIndistinguishable_WhetherOrNotBrowserSessionExists()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        // SignaCore completes preparation in the same shape when the browser's
        // upstream session is already gone; nothing here may infer which happened.
        f.LogoutDefect = "no-browser-session";
        using var response = await f.Logout(cookie);
        Assert.Equal("{\"success\":true,\"data\":{\"logoutUrl\":\"" + ExpectedLogoutUrl + "\"}}",
            await response.Content.ReadAsStringAsync(Ct));
        var (name, binding, _) = LogoutTransactionCookie(response);
        using var back = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal("/#/login?reason=logged_out", back.Headers.Location!.OriginalString);
        ReturnSafety(back);
    }

    [Fact]
    public async Task DocumentedRelativeLogoutUri_ResolvesAgainstTheTrustedIssuer()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        // SignaCore's preparation answer is a relative reference to its own
        // completion endpoint (IN-34). The envelope exposes the issuer-absolute
        // URI resolved from it, and the return trip is unchanged.
        f.LogoutDefect = "relative";
        using var response = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true,\"data\":{\"logoutUrl\":\"" + ExpectedLogoutUrl + "\"}}",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(1, f.LogoutPosts);
        var (relativeName, relativeBinding, _) = LogoutTransactionCookie(response);
        using var relativeReturn = await f.Return(
            "?state=" + f.LogoutForm["state"], relativeName + "=" + relativeBinding);
        Assert.Equal("/#/login?reason=logged_out", relativeReturn.Headers.Location!.OriginalString);
        ReturnSafety(relativeReturn);
        f.AssertNotLogged(f.LogoutForm["state"], relativeBinding, Fixture.Handle);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task WithoutConfiguredReturnRedirect_PreparationSendsHintOnly()
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = "" });
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(response));
        Assert.Equal(3, f.LogoutForm.Count);
        Assert.Equal(f.LastId, f.LogoutForm["id_token_hint"]);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal));
        using var back = await f.Client.GetAsync("/admin/auth/logout/return", Ct);
        Assert.Equal("/#/login?reason=logout_failed", back.Headers.Location!.OriginalString);
        ReturnSafety(back);
    }

    [Fact]
    public async Task RegisteredStaticQueryOnReturnUri_IsSentByteForByte()
    {
        const string configured = "https://lexarbor.test/admin/auth/logout/return?registered=1";
        using var f = new Fixture(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = configured });
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(response));
        Assert.Equal(5, f.LogoutForm.Count);
        Assert.Equal(configured, f.LogoutForm["post_logout_redirect_uri"]);
    }

    [Theory]
    [InlineData("http://lexarbor.test/admin/auth/logout/return")]
    [InlineData("https://lexarbor.test/admin/auth/callback")]
    [InlineData("https://lexarbor.test/admin/auth/logout/return?state=pre")]
    [InlineData("https://lexarbor.test/admin/auth/logout/return?x=1&x=2")]
    [InlineData("https://lexarbor.test/admin/auth/logout/return#f")]
    [InlineData("https://user@lexarbor.test/admin/auth/logout/return")]
    [InlineData("/admin/auth/logout/return")]
    [InlineData("https://lexarbor.test/ADMIN/AUTH/LOGOUT/RETURN")]
    public async Task InvalidReturnConfiguration_IsNeverSentUpstream(string configured)
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = configured });
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        // The upstream logout still happens, only without the unusable return pair.
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(response));
        Assert.Equal(3, f.LogoutForm.Count);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal));
        f.AssertSafeLogs();
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("400")]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("302")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("missing-uri")]
    [InlineData("non-string")]
    [InlineData("duplicate-field")]
    [InlineData("evil-host")]
    [InlineData("wrong-port")]
    [InlineData("wrong-path")]
    [InlineData("no-query")]
    [InlineData("bad-handle")]
    [InlineData("short-handle")]
    [InlineData("extra-query")]
    [InlineData("duplicate-handle")]
    [InlineData("fragment")]
    [InlineData("userinfo")]
    [InlineData("relative-evil-host")]
    [InlineData("relative-wrong-path")]
    [InlineData("relative-extra-query")]
    [InlineData("relative-bad-handle")]
    [InlineData("relative-fragment")]
    [InlineData("http-scheme")]
    public async Task UpstreamFailures_KeepLocalLogoutAndOmitLogoutUrl(string defect)
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        f.LogoutDefect = defect;
        using var response = await f.Logout(cookie);
        // The exact plain envelope: local-only logout, no upstream echo, no handle.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(1, f.LogoutPosts);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal));
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        f.AssertNotLogged("sensitive-upstream-marker", "sensitive-handle-marker", Fixture.Handle);
        f.AssertSafeLogs();
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("clientid")]
    [InlineData("issuer-query")]
    [InlineData("oversize-hint")]
    public async Task PreparationInputFailures_NeverReachUpstream(string defect)
    {
        var config = new Dictionary<string, string?>();
        if (defect == "secret") config["AdminAuthentication:OidcCode:ClientSecret"] = "";
        if (defect == "clientid") config["AdminAuthentication:OidcCode:ClientId"] = "";
        if (defect == "issuer-query") config["IdentityService:Issuer"] = "https://issuer.test/?tenant=1";
        using var f = new Fixture(config);
        var handle = await f.Seed(defect == "oversize-hint" ? new string('a', 8193) : "synthetic-old-id-marker");
        using var response = await f.Logout(AdminSessionCookie.Name + "=" + handle);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.LogoutPosts);
        Assert.Empty(f.LogoutForm);
        using var gone = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + handle);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("legacy")]
    [InlineData("bad-handle")]
    public async Task SessionlessLogouts_AreIdempotentWithoutUpstream(string kind)
    {
        using var f = new Fixture();
        var cookie = kind switch
        {
            "legacy" => VocabularyWebApplicationFactory.CookieName + "=" + f.Token("JWT", f.Claims()),
            "bad-handle" => AdminSessionCookie.Name + "=bad",
            _ => ""
        };
        using var response = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.LogoutPosts);
        AssertBothSessionCookiesDeleted(response);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task SessionWithoutIdToken_OrExpired_NeverCallsUpstream()
    {
        using var f = new Fixture();
        var tokenless = await f.Seed(idToken: null);
        using var first = await f.Logout(AdminSessionCookie.Name + "=" + tokenless);
        Assert.Equal("{\"success\":true}", await first.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.LogoutPosts);
        var expired = await f.Seed();
        f.Clock.Now += TimeSpan.FromMinutes(16);
        using var second = await f.Logout(AdminSessionCookie.Name + "=" + expired);
        Assert.Equal("{\"success\":true}", await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.LogoutPosts);
        using var gone = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + expired);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
    }

    [Fact]
    public async Task RepeatedLogout_NeverReplaysUpstream()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var first = await f.Logout(cookie);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(first));
        Assert.Equal(1, f.LogoutPosts);
        using var second = await f.Logout(cookie);
        Assert.Equal("{\"success\":true}", await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, f.LogoutPosts);
        using var third = await f.Logout();
        Assert.Equal("{\"success\":true}", await third.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, f.LogoutPosts);
    }

    [Fact]
    public async Task ConcurrentLogouts_TakeAtMostOneSnapshotAndOneUpstreamCall()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        f.LogoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = f.Logout(cookie);
        await f.LogoutEntered.Task.WaitAsync(Ct);
        // The snapshot was already atomically taken and revoked before the upstream
        // call began, so the second caller can only be local-only.
        using var second = await f.Logout(cookie);
        Assert.Equal("{\"success\":true}", await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, second.Headers.GetValues("Set-Cookie").Count(c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal)));
        f.LogoutGate.SetResult();
        using var first = await winner;
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(first));
        Assert.Equal(1, f.LogoutPosts);
    }

    [Fact]
    public async Task CancelDuringPreparation_KeepsLocalRevocationAndNeverRetries()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        f.LogoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var sending = f.Client.SendAsync(request, cancel.Token);
        await f.LogoutEntered.Task.WaitAsync(Ct);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        f.LogoutGate.TrySetResult();
        // The committed local revocation never rolls back and the upstream is never
        // retried; the outcome stays unknown and is never claimed.
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        Assert.Equal(1, f.LogoutPosts);
        using var repeat = await f.Logout(cookie);
        Assert.Equal("{\"success\":true}", await repeat.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, f.LogoutPosts);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task CookieLogoutWithoutCsrfHeader_403WithoutRevocationOrUpstream()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("CSRF", await response.Content.ReadAsStringAsync(Ct));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, f.LogoutPosts);
        using var session = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task BearerSelectedLogout_RevokesPresentedSessionButNeverFabricatesOne()
    {
        using var f = new Fixture();
        var bearer = f.Token("at+jwt", f.Claims());
        var plain = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        plain.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var withoutCookie = await f.Client.SendAsync(plain, Ct);
        Assert.Equal(HttpStatusCode.OK, withoutCookie.StatusCode);
        Assert.Equal("{\"success\":true}", await withoutCookie.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", withoutCookie.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.LogoutPosts);
        var cookie = await f.SignIn();
        var both = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        both.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        both.Headers.Add("Cookie", cookie);
        using var withCookie = await f.Client.SendAsync(both, Ct);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(withCookie));
        Assert.Equal(1, f.LogoutPosts);
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("marker")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("no-cookie")]
    [InlineData("wrong-binding")]
    public async Task InvalidReturnStates_AlwaysEndInTheSameFixedFailedRedirect(string defect)
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var logout = await f.Logout(cookie);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(logout));
        var state = f.LogoutForm["state"];
        var (name, binding, _) = LogoutTransactionCookie(logout);
        var query = defect switch
        {
            "missing" => "",
            "marker" => "?state=sensitive-state-marker",
            "empty" => "?state=",
            "duplicate" => "?state=" + state + "&state=" + state,
            "unknown" => "?state=" + RandomState(),
            _ => "?state=" + state
        };
        var returnCookie = defect switch
        {
            "no-cookie" => "",
            "wrong-binding" => name + "=" + RandomState(),
            _ => ""
        };
        using var response = await f.Return(query, returnCookie);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/#/login?reason=logout_failed", response.Headers.Location!.OriginalString);
        ReturnSafety(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("sensitive-state-marker", await response.Content.ReadAsStringAsync(Ct));
        // A mismatch or a missing cookie never consumes or destroys the real
        // transaction; only its own browser can still complete it.
        if (defect is "no-cookie" or "wrong-binding")
        {
            using var valid = await f.Return("?state=" + state, name + "=" + binding);
            Assert.Equal("/#/login?reason=logged_out", valid.Headers.Location!.OriginalString);
        }
        f.AssertNotLogged(state, binding, "sensitive-state-marker");
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task ExpiredReturnState_FailsClosed()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var logout = await f.Logout(cookie);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(logout));
        var (name, binding, _) = LogoutTransactionCookie(logout);
        f.Clock.Now += TimeSpan.FromMinutes(5);
        using var response = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal("/#/login?reason=logout_failed", response.Headers.Location!.OriginalString);
        ReturnSafety(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ReturnTransactions_RemainIndependent()
    {
        using var f = new Fixture();
        var firstHandle = await f.Seed("synthetic-first-id-marker");
        var secondHandle = await f.Seed("synthetic-second-id-marker");
        using var logoutA = await f.Logout(AdminSessionCookie.Name + "=" + firstHandle);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(logoutA));
        var stateA = f.LogoutForm["state"];
        var (nameA, bindingA, _) = LogoutTransactionCookie(logoutA);
        using var logoutB = await f.Logout(AdminSessionCookie.Name + "=" + secondHandle);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(logoutB));
        var stateB = f.LogoutForm["state"];
        var (nameB, bindingB, _) = LogoutTransactionCookie(logoutB);
        Assert.NotEqual(stateA, stateB);
        Assert.Equal(2, f.LogoutPosts);
        using var crossed = await f.Return("?state=" + stateA, nameA + "=" + bindingB);
        Assert.Equal("/#/login?reason=logout_failed", crossed.Headers.Location!.OriginalString);
        using var a = await f.Return("?state=" + stateA, nameA + "=" + bindingA);
        Assert.Equal("/#/login?reason=logged_out", a.Headers.Location!.OriginalString);
        Assert.Single(a.Headers.GetValues("Set-Cookie"));
        using var b = await f.Return("?state=" + stateB, nameB + "=" + bindingB);
        Assert.Equal("/#/login?reason=logged_out", b.Headers.Location!.OriginalString);
        using var replayA = await f.Return("?state=" + stateA, nameA + "=" + bindingA);
        Assert.Equal("/#/login?reason=logout_failed", replayA.Headers.Location!.OriginalString);
        f.AssertNotLogged(stateA, stateB, bindingA, bindingB);
    }

    [Fact]
    public async Task ExhaustedReturnStateStore_DegradesToReturnlessUpstreamLogout()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        for (var i = 0; i < PendingAdminLogoutStore.Capacity; i++)
            Assert.NotNull(f.Host.Services.GetRequiredService<PendingAdminLogoutStore>().Create(cancellationToken: Ct));
        using var response = await f.Logout(cookie);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(response));
        Assert.Equal(3, f.LogoutForm.Count);
        Assert.Equal(f.LastId, f.LogoutForm["id_token_hint"]);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/admin/auth/logout")]
    [InlineData("/admin/auth/logout/")]
    [InlineData("/ADMIN/AUTH/LOGOUT")]
    [InlineData("/Admin/Auth/Logout/")]
    public async Task LogoutRouteForms_CarryNoStoreOnEveryAcceptedForm(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.PostAsync(path, null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.LogoutPosts);
    }

    [Theory]
    [InlineData("/admin/auth/logout/return")]
    [InlineData("/admin/auth/logout/return/")]
    [InlineData("/ADMIN/AUTH/LOGOUT/RETURN")]
    [InlineData("/Admin/Auth/Logout/Return/")]
    public async Task ReturnRouteForms_CarrySafetyHeadersOnEveryAcceptedForm(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/#/login?reason=logout_failed", response.Headers.Location!.OriginalString);
        ReturnSafety(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
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
        Assert.Equal(0, f.LogoutPosts);
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
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(logout));
        var (name, binding, _) = LogoutTransactionCookie(logout);
        using var back = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal("/#/login?reason=logged_out", back.Headers.Location!.OriginalString);
        f.AssertNotLogged(f.LogoutForm["state"], binding, Fixture.Handle, Fixture.Secret, f.LastId, f.LastAccess);
    }

    private static async Task<string?> LogoutUrlOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("logoutUrl", out var url)
            && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
    }

    private static (string Name, string Binding, string Raw) LogoutTransactionCookie(HttpResponseMessage response)
    {
        var raw = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(LogoutCookiePrefix, StringComparison.Ordinal));
        var pair = raw.Split(';')[0];
        return (pair.Split('=')[0], pair.Split('=')[1], raw);
    }

    private static void AssertBothSessionCookiesDeleted(HttpResponseMessage response)
    {
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, c => c.StartsWith(AdminSessionCookie.Name + "=;", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith(VocabularyWebApplicationFactory.CookieName + "=;", StringComparison.Ordinal));
    }

    private static void ReturnSafety(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    private static string RandomState() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private sealed record Transaction(string State, string Cookie);

    private sealed class Fixture : IDisposable
    {
        public const string Issuer = "https://issuer.test";
        public const string Redirect = "https://lexarbor.test/admin/auth/callback?registered=1";
        public const string PostLogoutRedirect = "https://lexarbor.test/admin/auth/logout/return";
        public const string Secret = "synthetic-hosted-secret-marker";
        public static readonly string Code = WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes("synthetic-code-marker-0123456789"));
        // 32 bytes, canonical unpadded base64url: the shape the upstream contract fixes.
        public static readonly string Handle = WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes("synthetic-logout-handle-01234567"));
        private readonly RSA _rsa = RSA.Create(2048);
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client { get; }
        public Metadata Manager { get; }
        public Clock Clock { get; } = new();
        public Logs Logs { get; } = new();
        public string? LogoutDefect { get; set; }
        public string LastAccess { get; private set; } = "";
        public string LastId { get; private set; } = "";
        public ConcurrentDictionary<string, string> Challenges { get; } = new();
        public TaskCompletionSource? LogoutGate { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LogoutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _posts;
        public int Posts => _posts;
        private int _logoutPosts;
        public int LogoutPosts => _logoutPosts;
        public Dictionary<string, string> Form { get; private set; } = new();
        public Dictionary<string, string> LogoutForm { get; private set; } = new();

        public Fixture(Dictionary<string, string?>? extra = null, string provider = "OidcCode")
        {
            var config = new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = Issuer,
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = Secret,
                ["AdminAuthentication:OidcCode:RedirectUri"] = Redirect,
                ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = PostLogoutRedirect,
                ["AdminAuthentication:OidcCode:Scope"] = "openid profile",
                ["RateLimits:AdminLogin:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace"
            };
            if (extra is not null) foreach (var pair in extra) config[pair.Key] = pair.Value;
            Manager = new Metadata(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "key-1" });
            Base = new VocabularyWebApplicationFactory("Testing", true, provider, config);
            Host = Base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    { options.ConfigurationManager = Manager; options.TokenValidationParameters.IssuerSigningKey = Manager.Key; });
                    services.AddHttpClient(AdminCodeExchange.BackchannelName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
                    services.AddHttpClient(AdminPreparedLogout.BackchannelName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
                });
            });
            Client = Host.CreateClient(new() { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
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
            using var response = await Client.GetAsync("/admin/auth/start", Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            var state = parameters["state"].ToString();
            Challenges[parameters["code_challenge"].ToString()] = parameters["nonce"].ToString();
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            return new(state, cookie.Split(';')[0]);
        }

        public string Query(Transaction t) => "/admin/auth/callback?registered=1&state=" + t.State
            + "&iss=" + Uri.EscapeDataString(Issuer) + "&code=" + Code;

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

        public void AssertNotLogged(params string[] markers)
        {
            foreach (var marker in markers)
                if (marker.Length > 0) Assert.DoesNotContain(Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
        }

        public void AssertSafeLogs() => AssertNotLogged(Code, Secret, LastAccess, LastId, Handle,
            Form.GetValueOrDefault("code_verifier", ""), "sensitive-upstream-marker", "sensitive-handle-marker",
            "sensitive-state-marker", "synthetic-old-access-marker", "synthetic-old-id-marker",
            "synthetic-first-id-marker", "synthetic-second-id-marker",
            "synthetic-upstream-network-marker", "synthetic-upstream-timeout-marker");

        public void Dispose() { Client.Dispose(); Host.Dispose(); Base.Dispose(); _rsa.Dispose(); }

        private sealed class Handler(Fixture f) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri == Issuer + AdminPreparedLogout.LogoutRequestPath) return LogoutAsync(request, cancellationToken);
                Assert.Equal(Issuer + "/token", uri);
                return TokenAsync(request, cancellationToken);
            }

            private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref f._posts);
                Assert.Null(request.Headers.Authorization);
                var form = Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                f.Form = form;
                f.Entered.TrySetResult();
                // Select the nonce from the PKCE challenge: transaction-specific even under concurrent calls.
                var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
                var nonce = f.Challenges[challenge];
                var id = f.Claims(); id.Remove("role"); id["exp"] = f.Clock.Now.ToUnixTimeSeconds() + 300; id["name"] = "id-user"; id["nonce"] = nonce;
                var accessToken = f.Token("at+jwt", f.Claims());
                var idToken = f.Token("JWT", id);
                f.LastAccess = accessToken;
                f.LastId = idToken;
                return Json(new Dictionary<string, object>
                { ["access_token"] = accessToken, ["id_token"] = idToken, ["token_type"] = "Bearer", ["expires_in"] = 900, ["scope"] = "openid profile" });
            }

            private async Task<HttpResponseMessage> LogoutAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref f._logoutPosts);
                Assert.Equal(HttpMethod.Post, request.Method);
                // Confidential form authentication only, exactly like the token endpoint.
                Assert.Null(request.Headers.Authorization);
                f.LogoutForm = Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                f.LogoutEntered.TrySetResult();
                if (f.LogoutGate is not null) await f.LogoutGate.Task.WaitAsync(cancellationToken);
                var defect = f.LogoutDefect;
                if (defect == "network") throw new HttpRequestException("synthetic-upstream-network-marker");
                if (defect == "timeout") throw new TaskCanceledException("synthetic-upstream-timeout-marker");
                if (defect is "400" or "500" or "503")
                    return new((HttpStatusCode)int.Parse(defect))
                    { Content = JsonContent.Create(new { error = defect == "400" ? "invalid_request" : "server_error", error_description = "sensitive-upstream-marker" }) };
                if (defect == "302")
                    return new(HttpStatusCode.Found) { Content = new StringContent("{}"), Headers = { Location = new Uri("https://evil.test/") } };
                if (defect == "malformed") return Raw("{" + GoodUri());
                if (defect == "oversize") return Raw(new string('a', AdminPreparedLogout.MaximumResponseBytes + 1));
                if (defect == "missing-uri") return Raw("{}");
                if (defect == "non-string") return Raw("{\"logout_uri\":42}");
                if (defect == "duplicate-field") return Raw("{\"logout_uri\":" + Quote(GoodUri()) + ",\"logout_uri\":" + Quote(GoodUri()) + "}");
                // "no-browser-session" and any unknown value keep the ordinary success
                // shape: upstream answers identically either way.
                return Raw("{\"logout_uri\":" + Quote(defect switch
                {
                    "evil-host" => "https://evil.test/oauth2/logout?logout_handle=" + Handle,
                    "wrong-port" => "https://issuer.test:8443/oauth2/logout?logout_handle=" + Handle,
                    "wrong-path" => Issuer + "/oauth2/evil?logout_handle=" + Handle,
                    "no-query" => Issuer + "/oauth2/logout",
                    "bad-handle" => Issuer + "/oauth2/logout?logout_handle=sensitive-handle-marker!!!!",
                    "short-handle" => Issuer + "/oauth2/logout?logout_handle=" + Handle[..42],
                    "extra-query" => Issuer + "/oauth2/logout?logout_handle=" + Handle + "&x=1",
                    "duplicate-handle" => Issuer + "/oauth2/logout?logout_handle=" + Handle + "&logout_handle=" + Handle,
                    "fragment" => Issuer + "/oauth2/logout?logout_handle=" + Handle + "#f",
                    "userinfo" => "https://user:pass@issuer.test/oauth2/logout?logout_handle=" + Handle,
                    // SignaCore's documented success shape (IN-34): a relative
                    // reference to its own completion endpoint, resolved against
                    // the trusted issuer. The entries below it are the relative
                    // attacks that must not survive resolution.
                    "relative" => "/oauth2/logout?logout_handle=" + Handle,
                    "relative-evil-host" => "//evil.test/oauth2/logout?logout_handle=" + Handle,
                    "relative-wrong-path" => "/oauth2/evil?logout_handle=" + Handle,
                    "relative-extra-query" => "/oauth2/logout?logout_handle=" + Handle + "&x=1",
                    "relative-bad-handle" => "/oauth2/logout?logout_handle=sensitive-handle-marker!!!!",
                    "relative-fragment" => "/oauth2/logout?logout_handle=" + Handle + "#f",
                    "http-scheme" => "http://issuer.test/oauth2/logout?logout_handle=" + Handle,
                    _ => GoodUri()
                }) + "}");
            }

            private static string GoodUri() => Issuer + "/oauth2/logout?logout_handle=" + Handle;
            private static HttpResponseMessage Raw(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            private static string Quote(string value) => JsonSerializer.Serialize(value);
            private static Dictionary<string, string> Parse(string body) => body
                .Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
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
