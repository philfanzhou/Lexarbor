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
using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Prepared logout through the official client package: the local session always ends
/// first, only a verified one-time upstream logout URI reaches the envelope, and the
/// fixed return route answers only with in-site redirects. The fixture signs in through
/// the real hosted code flow so the persisted ID token used as the logout hint is a
/// genuine RS256 JWT. The logout endpoint carries the package's antiforgery boundary:
/// every logout request presents the token pair from <c>GET /admin/auth/csrf</c>.
/// </summary>
public class AdminHostedLogoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string LogoutReturnCookieName = "__Secure-Lexarbor.AdminSession-logout-return";
    private static string ExpectedLogoutUrl => SignaCoreAuthorityStub.Issuer + "/oauth2/logout?logout_handle=" + SignaCoreAuthorityStub.Handle;

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
        Assert.DoesNotContain(SignaCoreAuthorityStub.Secret, body);
        // Exactly one preparation, with the confidential Basic authentication and the
        // genuine signed-in ID token as the hint; never a form credential.
        Assert.Equal(1, f.LogoutPosts);
        Assert.Equal(3, f.LogoutForm.Count);
        Assert.Equal(f.LastId, f.LogoutForm["id_token_hint"]);
        Assert.Equal(SignaCoreAuthorityStub.PostLogoutRedirect, f.LogoutForm["post_logout_redirect_uri"]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "client-id:" + SignaCoreAuthorityStub.Secret)), f.Authority.LastAuthorizationHeader);
        Assert.True(CanonicalState(f.LogoutForm["state"]));
        // Both session cookies are deleted; the correlation cookie is HttpOnly,
        // Secure, Lax, path-bound, five-minute and carries only the correlation id.
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        // Three cookie names: the session and the retired password-login cookie are
        // both deleted (the package's own session deletion may appear beside the
        // middleware's), and the one-time correlation cookie is set.
        Assert.Equal(
            new[] { AdminSessionCookie.Name, "__Secure-Lexarbor.AdminSession-logout-return", VocabularyWebApplicationFactory.CookieName }.OrderBy(name => name, StringComparer.Ordinal),
            cookies.Select(c => c.Split('=')[0]).Distinct().OrderBy(name => name, StringComparer.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith(AdminSessionCookie.Name + "=;", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith(VocabularyWebApplicationFactory.CookieName + "=;", StringComparison.Ordinal));
        var (name, binding, raw) = LogoutReturnCookie(response);
        Assert.Equal(LogoutReturnCookieName, name);
        Assert.Contains("httponly", raw); Assert.Contains("secure", raw); Assert.Contains("samesite=lax", raw);
        Assert.Contains("path=/admin/auth/logout", raw); Assert.DoesNotContain("domain=", raw);
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
        // The consumed state never works twice; the correlation cookie is finished
        // either way and nothing else is touched.
        using var replay = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal("/#/login?reason=logout_failed", replay.Headers.Location!.OriginalString);
        ReturnSafety(replay);
        Assert.DoesNotContain(replay.Headers.GetValues("Set-Cookie"), c => !c.StartsWith(LogoutReturnCookieName + "=;", StringComparison.Ordinal));
        f.AssertNotLogged(f.LogoutForm["state"], binding, SignaCoreAuthorityStub.Handle);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task UpstreamAnswer_IsIndistinguishable_WhetherOrNotBrowserSessionExists()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        // SignaCore completes preparation in the same shape when the browser's
        // upstream session is already gone; nothing here may infer which happened.
        f.Authority.LogoutDefect = "no-browser-session";
        using var response = await f.Logout(cookie);
        Assert.Equal("{\"success\":true,\"data\":{\"logoutUrl\":\"" + ExpectedLogoutUrl + "\"}}",
            await response.Content.ReadAsStringAsync(Ct));
        var (name, binding, _) = LogoutReturnCookie(response);
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
        f.Authority.LogoutDefect = "relative";
        using var response = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true,\"data\":{\"logoutUrl\":\"" + ExpectedLogoutUrl + "\"}}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(1, f.LogoutPosts);
        var (relativeName, relativeBinding, _) = LogoutReturnCookie(response);
        using var relativeReturn = await f.Return(
            "?state=" + f.LogoutForm["state"], relativeName + "=" + relativeBinding);
        Assert.Equal("/#/login?reason=logged_out", relativeReturn.Headers.Location!.OriginalString);
        ReturnSafety(relativeReturn);
        f.AssertNotLogged(f.LogoutForm["state"], relativeBinding, SignaCoreAuthorityStub.Handle);
        f.AssertSafeLogs();
    }

    [Fact]
    public async Task WithoutConfiguredReturnRedirect_PreparationSendsHintOnly()
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = "" });
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(response));
        Assert.Single(f.LogoutForm);
        Assert.Equal(f.LastId, f.LogoutForm["id_token_hint"]);
        // Without a registered return redirect the state never travels upstream, so
        // the fixed return route cannot complete: the failed redirect stands.
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
        // A registered static query or any other illegal shape is a startup failure
        // now: the official validator accepts no query on the redirect URIs.
        var config = new Dictionary<string, string?>
        { ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = configured };
        using var f = new Fixture(config, expectStartupFailure: true);
        Assert.True(f.StartupFailed);
    }

    // The official resolver checks the origin triple and the single canonical
    // logout_handle query, not the path: a same-origin wrong path is presented, not
    // discarded, and the browser stays on the trusted issuer.
    [Theory]
    [InlineData("wrong-path")]
    [InlineData("relative-wrong-path")]
    public async Task SameOriginWrongPathLogoutUri_IsPresentedFromTheTrustedIssuer(string defect)
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        f.Authority.LogoutDefect = defect;
        using var response = await f.Logout(cookie);
        Assert.Equal(SignaCoreAuthorityStub.Issuer, new Uri(await LogoutUrlOf(response)).GetLeftPart(UriPartial.Authority));
        Assert.Equal(1, f.LogoutPosts);
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
    [InlineData("no-query")]
    [InlineData("bad-handle")]
    [InlineData("short-handle")]
    [InlineData("extra-query")]
    [InlineData("duplicate-handle")]
    [InlineData("fragment")]
    [InlineData("userinfo")]
    [InlineData("relative-evil-host")]
    [InlineData("relative-extra-query")]
    [InlineData("relative-bad-handle")]
    [InlineData("relative-fragment")]
    [InlineData("http-scheme")]
    public async Task UpstreamFailures_KeepLocalLogoutAndOmitLogoutUrl(string defect)
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        f.Authority.LogoutDefect = defect;
        using var response = await f.Logout(cookie);
        // The exact plain envelope: local-only logout, no upstream echo, no handle.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(1, f.LogoutPosts);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(LogoutReturnCookieName, StringComparison.Ordinal));
        using var gone = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        f.AssertNotLogged("sensitive-upstream-marker", "sensitive-handle-marker", SignaCoreAuthorityStub.Handle);
        f.AssertSafeLogs();
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("clientid")]
    [InlineData("oversize-hint")]
    public async Task UnusablePreparationInputs_EndLocalWithoutUpstreamSuccess(string defect)
    {
        var config = new Dictionary<string, string?>();
        if (defect == "secret") config["AdminAuthentication:OidcCode:ClientSecret"] = "";
        if (defect == "clientid") config["AdminAuthentication:OidcCode:ClientId"] = "";
        using var f = new Fixture(config);
        var handle = await f.Seed(defect == "oversize-hint" ? new string('a', 8193) : "synthetic-old-id-marker");
        using var response = await f.Logout(AdminSessionCookie.Name + "=" + handle);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Null(await LogoutUrlOf(response));
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
            "legacy" => VocabularyWebApplicationFactory.CookieName + "=" + f.Authority.Token("JWT", f.Authority.AccessClaims()),
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
    public async Task SessionWithoutIdToken_OrExpired_EndsLocally()
    {
        using var f = new Fixture();
        var tokenless = await f.Seed(idToken: null);
        using var first = await f.Logout(AdminSessionCookie.Name + "=" + tokenless);
        Assert.Equal("{\"success\":true}", await first.Content.ReadAsStringAsync(Ct));
        // The unusable hint is sent once and refused by the authority: local-only.
        Assert.Null(await LogoutUrlOf(first));
        Assert.Equal(1, f.LogoutPosts);
        var expired = await f.Seed();
        f.Clock.Now += TimeSpan.FromMinutes(16);
        using var second = await f.Logout(AdminSessionCookie.Name + "=" + expired);
        Assert.Equal("{\"success\":true}", await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, f.LogoutPosts);
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
        f.Authority.LogoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = f.Logout(cookie);
        await f.Authority.LogoutEntered.Task.WaitAsync(Ct);
        // The snapshot was already atomically taken and revoked before the upstream
        // call began; the package's per-key stripe serializes the second logout of
        // the same session behind the first, and it then finds nothing left to do.
        var second = f.Logout(cookie);
        f.Authority.LogoutGate.SetResult();
        using var first = await winner;
        using var secondResponse = await second;
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(first));
        Assert.Equal("{\"success\":true}", await secondResponse.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, secondResponse.Headers.GetValues("Set-Cookie").Count(c => c.StartsWith(LogoutReturnCookieName, StringComparison.Ordinal)));
        Assert.Equal(1, f.LogoutPosts);
    }

    [Fact]
    public async Task CancelDuringPreparation_KeepsLocalRevocationAndNeverRetries()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        f.Authority.LogoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sending = f.LogoutAsync(cookie, cancel.Token);
        await f.Authority.LogoutEntered.Task.WaitAsync(Ct);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        f.Authority.LogoutGate.TrySetResult();
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
    public async Task LogoutWithoutAntiforgeryToken_IsRejectedWithoutRevocationOrUpstream()
    {
        using var f = new Fixture();
        var cookie = await f.SignIn();
        using var response = await f.Logout(cookie, csrf: false);
        // The package's fixed antiforgery rejection: 400, no session touched.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("csrf_rejected", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.LogoutPosts);
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
        Assert.Equal(0, f.LogoutPosts);
        var cookie = await f.SignIn();
        var both = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        both.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        both.Headers.Add(AdminTestAntiforgery.HeaderName, token);
        both.Headers.Add("Cookie", cookie + "; " + antiforgery);
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
        var (name, binding, _) = LogoutReturnCookie(logout);
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
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => !c.StartsWith(LogoutReturnCookieName + "=;", StringComparison.Ordinal));
        Assert.DoesNotContain("sensitive-state-marker", await response.Content.ReadAsStringAsync(Ct));
        // A mismatch or a missing cookie never consumes or destroys the real
        // transaction; only its own browser can still complete it. (A presented
        // correlation id is consumed by its own attempt, exactly once.)
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
        var (name, binding, _) = LogoutReturnCookie(logout);
        f.Clock.Now += TimeSpan.FromMinutes(5);
        using var response = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal("/#/login?reason=logout_failed", response.Headers.Location!.OriginalString);
        ReturnSafety(response);
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
        var (nameA, bindingA, _) = LogoutReturnCookie(logoutA);
        using var logoutB = await f.Logout(AdminSessionCookie.Name + "=" + secondHandle);
        Assert.Equal(ExpectedLogoutUrl, await LogoutUrlOf(logoutB));
        var stateB = f.LogoutForm["state"];
        var (nameB, bindingB, _) = LogoutReturnCookie(logoutB);
        Assert.NotEqual(stateA, stateB);
        Assert.Equal(2, f.LogoutPosts);
        using var a = await f.Return("?state=" + stateA, nameA + "=" + bindingA);
        Assert.Equal("/#/login?reason=logged_out", a.Headers.Location!.OriginalString);
        using var b = await f.Return("?state=" + stateB, nameB + "=" + bindingB);
        Assert.Equal("/#/login?reason=logged_out", b.Headers.Location!.OriginalString);
        // A crossed attempt with the right correlation but the wrong state consumes
        // the one-time correlation exactly once — the official store removes the
        // entry whether the presented state matched or not.
        using var crossed = await f.Return("?state=" + stateB, nameA + "=" + bindingA);
        Assert.Equal("/#/login?reason=logout_failed", crossed.Headers.Location!.OriginalString);
        using var replayA = await f.Return("?state=" + stateA, nameA + "=" + bindingA);
        Assert.Equal("/#/login?reason=logout_failed", replayA.Headers.Location!.OriginalString);
        f.AssertNotLogged(stateA, stateB, bindingA, bindingB);
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
        var (name, binding, _) = LogoutReturnCookie(logout);
        using var back = await f.Return("?state=" + f.LogoutForm["state"], name + "=" + binding);
        Assert.Equal("/#/login?reason=logged_out", back.Headers.Location!.OriginalString);
        f.AssertNotLogged(f.LogoutForm["state"], binding, SignaCoreAuthorityStub.Handle, SignaCoreAuthorityStub.Secret, f.LastId, f.LastAccess);
    }

    private static async Task<string?> LogoutUrlOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("logoutUrl", out var url)
            && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
    }

    private static (string Name, string Binding, string Raw) LogoutReturnCookie(HttpResponseMessage response)
    {
        var raw = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(LogoutReturnCookieName + "=", StringComparison.Ordinal));
        var pair = raw.Split(';')[0];
        return (pair.Split('=')[0], pair.Split('=')[1], raw);
    }

    private static bool CanonicalState(string? value)
    {
        if (value is null || value.Length != 43 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value);
            return bytes.Length == 32 && WebEncoders.Base64UrlEncode(bytes) == value;
        }
        catch (FormatException) { return false; }
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
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client { get; }
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
                    services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
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
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            return new(state, cookie.Split(';')[0]);
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

        public int LogoutPosts => Authority.LogoutPosts;
        public Dictionary<string, string> LogoutForm => Authority.LogoutForm;
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
