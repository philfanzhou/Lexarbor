using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

public class AdminHostedLoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealCodeFlow_UsesExactFormEncryptedSessionAndAdminReadWrite()
    {
        using var f = new Fixture();
        var t = await f.Start("/books/book_1/words");
        Assert.Equal(8, t.Parameters.Count);
        Assert.All(t.Parameters, pair => Assert.Single(pair.Value));
        Assert.Equal("code", t.Parameters["response_type"]);
        Assert.Equal(SignaCoreAuthorityStub.Redirect, t.Parameters["redirect_uri"]);
        Assert.Equal("openid profile", t.Parameters["scope"]);
        Assert.Equal("S256", t.Parameters["code_challenge_method"]);
        // The code flow's standard response mode is query, and the handler correctly omits
        // the response_mode parameter for it: the authorize redirect carries exactly the
        // eight parameters of the replaced slice's wire contract.
        Assert.DoesNotContain("response_mode", t.Parameters.Keys);
        Assert.DoesNotContain("x-client-SKU", t.Parameters.Keys);
        // The framework's carrier cookies (correlation and nonce): HttpOnly, Lax, scoped to
        // the callback path and bounded by the replaced slice's five-minute handshake window.
        Assert.Equal(2, t.Cookies.Length);
        Assert.Contains(t.Cookies, cookie => cookie.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        Assert.Contains(t.Cookies, cookie => cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal));
        foreach (var raw in t.SetCookies)
        {
            Assert.Contains("secure", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/admin/auth/callback", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain=", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("expires=", raw, StringComparison.OrdinalIgnoreCase);
        }
        using var callback = await f.Send(f.Query(t) + "&returnUrl=https%3A%2F%2Fevil.test%2Fignored", t.Cookie);
        Assert.Equal("/#/books/book_1/words", callback.Headers.Location!.OriginalString);
        Assert.Equal("no-store", callback.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", callback.Headers.GetValues("Referrer-Policy").Single());
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(s => s.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        Assert.DoesNotContain(f.LastAccess, cookie); Assert.DoesNotContain(f.LastId, cookie);
        // Both single-use carrier cookies are deleted with the completed sign-in.
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), s => s.StartsWith(t.Cookies[0].Split('=')[0] + "=;", StringComparison.Ordinal));
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), s => s.StartsWith(t.Cookies[1].Split('=')[0] + "=;", StringComparison.Ordinal));
        using var session = await f.Send("/admin/auth/session", cookie);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Contains("access-user", await session.Content.ReadAsStringAsync(Ct));
        using var read = await f.Send("/admin/vocabulary-books", cookie);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await f.Send("/admin/vocabulary-books", cookie, "POST", JsonContent.Create(new { bookName = "Hosted book", status = true }), csrf: true);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal(1, f.TokenPosts);
        Assert.Equal(SignaCoreAuthorityStub.Code, f.TokenForm["code"]);
        Assert.Equal("authorization_code", f.TokenForm["grant_type"]);
        Assert.Equal(SignaCoreAuthorityStub.Redirect, f.TokenForm["redirect_uri"]);
        // client_secret_post: the framework's default client authentication. The secret rides
        // the form of the confidential backchannel request and never reaches the browser.
        Assert.Equal(6, f.TokenForm.Count);
        Assert.Equal("client-id", f.TokenForm["client_id"]);
        Assert.Equal(SignaCoreAuthorityStub.Secret, f.TokenForm["client_secret"]);
        Assert.Null(f.Authority.LastAuthorizationHeader);
        Assert.Equal(t.Parameters["code_challenge"], WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(f.TokenForm["code_verifier"]))));
        using var scope = f.Host.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.SingleAsync(Ct);
        Assert.DoesNotContain(f.LastAccess, row.ProtectedPayload); Assert.DoesNotContain(f.LastId, row.ProtectedPayload);
        var saved = await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().ReadAsync(AdminSessionTestCookieReads.HandleOf(f.Host.Services, cookie), Ct);
        Assert.Equal(f.LastId, saved!.IdToken); Assert.Equal(f.LastAccess, saved.AccessToken);
        f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task InternalOidcCalls_CarryTheRequestCorrelationId()
    {
        // The token exchange is the canonical internal service call: a request
        // that arrives with a correlation id passes the very same id to the
        // identity provider, so one id follows an administrator's sign-in across both
        // logs. The id is log-correlation material only, exactly as documented.
        using var f = new Fixture();
        var t = await f.Start();
        using var callback = await f.Send(f.Query(t), t.Cookie, correlationId: "correlation-marker-42");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        Assert.Equal(1, f.TokenPosts);
        Assert.Equal("correlation-marker-42", f.Authority.LastCorrelationId);
    }

    [Fact]
    public async Task InternalOidcCalls_WithoutInboundIdCarryTheAssignedOne()
    {
        // Without an inbound header the ServiceMantle middleware assigns one
        // per request; the internal call still carries an id — the assigned
        // one, the same value the response echoes back.
        using var f = new Fixture();
        var t = await f.Start();
        using var callback = await f.Send(f.Query(t), t.Cookie);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        Assert.Equal(1, f.TokenPosts);
        var assigned = callback.Headers.GetValues("x-correlation-id").Single();
        Assert.NotEmpty(assigned);
        Assert.Equal(assigned, f.Authority.LastCorrelationId);
    }

    [Theory]
    [InlineData(null, 302)]
    [InlineData("/books", 302)]
    [InlineData("/settings", 302)]
    [InlineData("/settings/audio-storage", 302)]
    [InlineData("/settings/review", 302)]
    [InlineData("/settings/text", 302)]
    [InlineData("/settings/speech", 302)]
    [InlineData("/settings/background", 302)]
    [InlineData("/settings/unknown", 400)]
    [InlineData("/settings/review/", 400)]
    [InlineData("/settings/review?next=https://evil.test", 400)]
    [InlineData("/enrichment", 302)]
    [InlineData("/#/enrichment", 302)]
    [InlineData("/enrichment/", 400)]
    [InlineData("/Enrichment", 400)]
    [InlineData("/enrichment?wordId=x", 400)]
    [InlineData("/enrichment#x", 400)]
    [InlineData("/enrichment%2f", 400)]
    [InlineData("/%65nrichment", 400)]
    [InlineData("/enrichment\n", 400)]
    [InlineData("/enrichment\\other", 400)]
    [InlineData("//evil.test/enrichment", 400)]
    [InlineData("/books/book_1-X/audio", 400)]
    [InlineData("/#/books/book_1-X/audio", 400)]
    [InlineData("/books/../audio", 400)]
    [InlineData("/books/a%2Fb/audio", 400)]
    [InlineData("/books/a/audio?x=1", 400)]
    [InlineData("/books/a/audio#x", 400)]
    [InlineData("/books/a/audio/", 400)]
    [InlineData("/books/a/audio\n", 400)]
    [InlineData("/books/a/audio\r", 400)]
    [InlineData("/books/a\\b/audio", 400)]
    [InlineData("/books/a\n/audio", 400)]
    [InlineData("/books/中文/audio", 400)]
    [InlineData("/books/a/other", 400)]
    [InlineData("/%73ettings", 400)]
    [InlineData("/settings?x=1", 400)]
    [InlineData("/#/import/batch", 302)]
    [InlineData("https://evil.test", 400)]
    [InlineData("//evil.test", 400)]
    [InlineData("/books?x=1", 400)]
    [InlineData("/books%2Fbad", 400)]
    [InlineData("/books#bad", 400)]
    [InlineData("/books/../words", 400)]
    [InlineData("", 400)]
    public async Task Start_ReturnAllowlistDecodesOnlyTransportOnce(string? target, int status)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync("/admin/auth/start" + (target is null ? "" : "?returnUrl=" + Uri.EscapeDataString(target)), Ct);
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 400) Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public void BookWordsReturnTarget_RequiresExactSafeIdLength()
    {
        var safe = "/books/" + new string('a', 128) + "/words";
        Assert.Equal("/#" + safe, AdminLoginReturnTarget.Normalize(safe));
        Assert.Null(AdminLoginReturnTarget.Normalize("/books/" + new string('a', 129) + "/words"));
        Assert.Null(AdminLoginReturnTarget.Normalize("/books//words"));
    }

    [Theory]
    [InlineData("/enrichment")]
    [InlineData("/#/enrichment")]
    public async Task EnrichmentReturn_RealCallbackUsesStoredExactTargetAndSession(string target)
    {
        using var f = new Fixture();
        var transaction = await f.Start(target);
        using var callback = await f.Send(f.Query(transaction) + "&returnUrl=https%3A%2F%2Fevil.test", transaction.Cookie);
        Assert.Equal("/#/enrichment", callback.Headers.Location!.OriginalString);
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(s => s.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        using var state = await f.Send("/admin/enrichment/summary", cookie);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Equal(1, f.TokenPosts);
        f.AssertSafeLogs(transaction);
    }

    [Theory]
    [InlineData("config", 503)]
    [InlineData("discovery", 502)]
    [InlineData("untrusted", 502)]
    [InlineData("duplicate-return", 400)]
    public async Task Start_FailsWithoutCorrelationCookieOrSession(string failure, int status)
    {
        var config = new Dictionary<string, string?>();
        if (failure == "config") config["AdminAuthentication:OidcCode:ClientSecret"] = "";
        using var f = new Fixture(config);
        f.Authority.DiscoveryFails = failure == "discovery";
        f.Authority.CrossOriginAuthorizationEndpoint = failure == "untrusted";
        using var response = await f.Client.GetAsync("/admin/auth/start" + (failure == "duplicate-return" ? "?returnUrl=/books&returnUrl=/books" : ""), Ct);
        Assert.Equal(status, (int)response.StatusCode); Assert.False(response.Headers.Contains("Set-Cookie")); Assert.Equal(0, f.TokenPosts);
        Assert.DoesNotContain(SignaCoreAuthorityStub.Secret, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("https://lexarbor.test/admin/auth/callback?state=bad")]
    [InlineData("https://lexarbor.test/admin/auth/callback?x=1&x=2")]
    [InlineData("https://lexarbor.test/admin/auth/callback#f")]
    public async Task ConfiguredButIllegalRedirectUri_FailsStartup(string redirect)
    {
        // A configured but illegal protocol option is a startup failure — the
        // missing-versus-illegal split of the unconfigured mode. The plain-http form is no
        // longer illegal; its topology is covered positively by AdminHostedLoginPlainHttpTests.
        using var f = new Fixture(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:RedirectUri"] = redirect }, expectStartupFailure: true);
        Assert.True(f.StartupFailed);
    }

    // The strict authorization-response shape is checked on the raw query before the
    // framework consumes any correlation state, so every shape defect below leaves the
    // browser's carrier cookies intact and its handshake replayable; the correlation-binding
    // and redemption defects consume the one-time carrier cookies with the response.
    [Theory]
    [InlineData("state", "sign_in_failed", false)]
    [InlineData("browser", "sign_in_failed", true)]
    [InlineData("issuer", "sign_in_failed", false)]
    [InlineData("duplicate-state", "sign_in_failed", false)]
    [InlineData("duplicate-code", "sign_in_failed", false)]
    [InlineData("duplicate-issuer", "sign_in_failed", false)]
    [InlineData("both", "canceled", true)]
    [InlineData("no-result", "sign_in_failed", false)]
    [InlineData("malformed-code", "sign_in_failed", true)]
    [InlineData("unknown-error", "sign_in_failed", true)]
    [InlineData("duplicate-error", "sign_in_failed", false)]
    public async Task InvalidCallback_NeverRedeemsOrDeletesAnotherTransaction(string defect, string reason, bool consumed)
    {
        using var f = new Fixture(); var t = await f.Start();
        var query = f.Query(t);
        var cookie = t.Cookie;
        switch (defect)
        {
            case "state": query = query.Replace(t.State, SignaCoreAuthorityStub.Code, StringComparison.Ordinal); break;
            case "browser": cookie = cookie.Split(';')[0].Split('=')[0] + "=" + SignaCoreAuthorityStub.Code; break;
            case "issuer": query = query.Replace(Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer), Uri.EscapeDataString("https://evil.test"), StringComparison.Ordinal); break;
            case "duplicate-state": query += "&state=" + t.State; break;
            case "duplicate-code": query += "&code=" + SignaCoreAuthorityStub.Code; break;
            case "duplicate-issuer": query += "&iss=" + Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer); break;
            case "both": query += "&error=access_denied"; break;
            case "no-result": query = query.Replace("&code=" + SignaCoreAuthorityStub.Code, "", StringComparison.Ordinal); break;
            case "malformed-code": query = query.Replace(SignaCoreAuthorityStub.Code, "sensitive-code-marker", StringComparison.Ordinal); break;
            case "unknown-error": query = f.Query(t, "sensitive-unknown-error-marker"); break;
            case "duplicate-error": query = f.Query(t, "access_denied") + "&error=access_denied"; break;
        }
        using var response = await f.Send(query, cookie);
        Failure(response, reason);
        // A rejection before the correlation consumption carries no session cookie at all;
        // every later shape deletes only the transaction's own carrier cookies.
        if (response.Headers.Contains("Set-Cookie"))
            Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
        if (!consumed)
        {
            using var valid = await f.Callback(t);
            Assert.Equal("/#/books", valid.Headers.Location!.OriginalString);
        }
        else
        {
            // The browser's carrier cookie was deleted with the rejection, so its own
            // replay presents no binding and cannot complete the handshake.
            using var replay = await f.Send(f.Query(t), t.Cookies[0].Split('=')[0] + "=");
            Failure(replay, "sign_in_failed");
        }
        f.AssertSafeLogs(t);
    }

    [Theory]
    [InlineData("access_denied", "canceled")]
    [InlineData("invalid_scope", "sign_in_failed")]
    [InlineData("server_error", "provider_unavailable")]
    [InlineData("temporarily_unavailable", "provider_unavailable")]
    public async Task UpstreamErrors_ConsumeAndCleanOnlyOwnCookie(string error, string reason)
    {
        using var f = new Fixture(); var t = await f.Start(); var other = await f.Start();
        using var response = await f.Send(f.Query(t, error) + "&error_description=sensitive-description-marker", t.Cookie + "; " + other.Cookie);
        Failure(response, reason); Assert.Equal(0, f.TokenPosts);
        // The browser's own carrier cookies were deleted with the response, so its own
        // replay cannot complete the consumed handshake; the other browser's
        // transaction is untouched and independent.
        using var replay = await f.Send(f.Query(t), t.Cookies[0].Split('=')[0] + "=");
        Failure(replay, "sign_in_failed"); Assert.Equal(0, f.TokenPosts);
        using var independent = await f.Callback(other); Assert.Equal("/#/books", independent.Headers.Location!.OriginalString);
        f.AssertSafeLogs(t);
    }

    [Theory]
    [InlineData("nonadmin", "denied")]
    [InlineData("id-role", "denied")]
    [InlineData("nonce", "sign_in_failed")]
    [InlineData("network", "provider_unavailable")]
    [InlineData("timeout", "provider_unavailable")]
    [InlineData("307", "sign_in_failed")]
    [InlineData("308", "sign_in_failed")]
    [InlineData("missing-id", "sign_in_failed")]
    [InlineData("missing-access", "sign_in_failed")]
    [InlineData("malformed", "sign_in_failed")]
    [InlineData("invalid-grant", "sign_in_failed")]
    [InlineData("expires", "sign_in_failed")]
    [InlineData("oversize-token", "sign_in_failed")]
    [InlineData("oversize-body", "sign_in_failed")]
    [InlineData("bad-token", "sign_in_failed")]
    public async Task FailedCallback_PreservesBothExistingSessionKinds(string defect, string reason)
    {
        using var f = new Fixture();
        var old = await f.Seed(); var legacy = f.Authority.Token("JWT", f.Authority.AccessClaims("admin"));
        var t = await f.Start(); f.Authority.TokenDefect = defect;
        using var response = await f.Callback(t, f.Cookie(old) + "; lexarborAdmin=" + legacy);
        Failure(response, reason);
        using var existing = await f.Send("/admin/auth/session", f.Cookie(old)); Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        // The legacy password-login JWT cookie authenticates nothing anymore.
        using var oldSession = await f.Send("/admin/auth/session", "lexarborAdmin=" + legacy); Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        // The browser's carrier cookies were consumed with the failed attempt (the framework
        // deletes the correlation cookie with the response); an honest replay presents no
        // binding and cannot redeem the code again.
        using var replay = await f.Send(f.Query(t), t.Cookies[0].Split('=')[0] + "=");
        Failure(replay, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        f.AssertSafeLogs(t);
    }

    // Declared tolerances of the framework handler's standard OIDC validation (the replaced
    // package applied some of them too): a token_type the response carries is not interpreted,
    // a scope echo beyond the request is not client-policed, and both defects complete the
    // sign-in. A body that is not JSON at all stays a sign-in failure (see the failed-callback
    // matrix).
    [Theory]
    [InlineData("token-type")]
    [InlineData("scope")]
    public async Task FrameworkToleratedTokenResponses_CompleteSignIn(string defect)
    {
        using var f = new Fixture();
        var t = await f.Start(); f.Authority.TokenDefect = defect;
        using var response = await f.Callback(t);
        Assert.Equal("/#/books", response.Headers.Location!.OriginalString);
        using var scope = f.Host.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
    }

    // The ID token is validated by the framework handler against the published JWKS; the
    // access token is re-verified through the host's JwtBearer trust at the pre-sign-in gate
    // and again in the session store. Each defect below must fail one of those paths before
    // any session is written. The framework's protocol validator demands a present iat claim,
    // so that defect is rejected here even though the replaced package tolerated it.
    [Theory]
    [InlineData("id", "aud")]
    [InlineData("access", "aud")]
    [InlineData("id", "iss")]
    [InlineData("access", "iss")]
    [InlineData("id", "signature")]
    [InlineData("access", "signature")]
    [InlineData("id", "missing-iat")]
    [InlineData("id", "missing-exp")]
    [InlineData("id", "missing-sub")]
    [InlineData("id", "empty-sub")]
    [InlineData("access", "missing-sub")]
    [InlineData("id", "alg")]
    [InlineData("access", "alg")]
    [InlineData("access", "nbf")]
    public async Task BothRealJwtTrustPaths_MustPassBeforeSignIn(string kind, string defect)
    {
        using var f = new Fixture(); var t = await f.Start(); f.Authority.TokenDefect = kind + ":" + defect;
        using var response = await f.Callback(t); Failure(response, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        using var scope = f.Host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
        f.AssertSafeLogs(t);
    }

    // Standard OIDC tolerance of the framework paths: a key selected without kid (the single
    // published key), a multi-valued audience containing the client, repeated members inside
    // a compact JWT (last value wins), a missing access-token nbf, and boundary lifetimes
    // inside the documented 30-second clock tolerance all complete the sign-in.
    [Theory]
    [InlineData("id", "kid")]
    [InlineData("access", "kid")]
    [InlineData("id", "typ")]
    [InlineData("access", "typ")]
    [InlineData("id", "missing-nbf")]
    [InlineData("access", "missing-nbf")]
    [InlineData("id", "duplicate-iat")]
    [InlineData("id", "duplicate-nonce")]
    [InlineData("id", "duplicate-sub")]
    [InlineData("access", "duplicate-sub")]
    [InlineData("access", "duplicate-exp")]
    [InlineData("id", "extra-aud")]
    [InlineData("access", "extra-aud")]
    [InlineData("id", "exp")]
    [InlineData("access", "exp")]
    [InlineData("access", "iat")]
    [InlineData("id", "sub")]
    public async Task StandardOidcTolerance_StillSignsIn(string kind, string defect)
    {
        using var f = new Fixture(); var t = await f.Start(); f.Authority.TokenDefect = kind + ":" + defect;
        using var response = await f.Callback(t);
        Assert.Equal("/#/books", response.Headers.Location!.OriginalString);
        using var scope = f.Host.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentCallbacks_OnePostPerTransactionAndIndependentSignIns()
    {
        using var f = new Fixture();
        var starts = await Task.WhenAll(f.Start(), f.Start()); var first = starts[0]; var second = starts[1];
        Assert.NotEqual(first.State, second.State); Assert.NotEqual(first.Cookie, second.Cookie);
        f.Authority.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = f.Callback(first); await f.Authority.Entered.Task.WaitAsync(Ct);
        // The replay races the held redemption over the same handshake: the authority's own
        // one-time code (keyed to the handshake's verifier) refuses the second use, and the
        // refused attempt answers the fixed failure without touching the pending session.
        using var replay = await f.Callback(first); Failure(replay, "sign_in_failed"); Assert.Equal(2, f.TokenPosts);
        var two = f.Callback(second);
        while (f.TokenPosts < 3) await Task.Delay(10, Ct);
        f.Authority.Gate.SetResult(); using var r1 = await one; using var r2 = await two;
        Assert.Equal("/#/books", r1.Headers.Location!.OriginalString); Assert.Equal("/#/books", r2.Headers.Location!.OriginalString);
        using var scope = f.Host.Services.CreateScope(); Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.CountAsync(Ct));
        Assert.NotEqual(r1.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)), r2.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("")]
    [InlineData("{\"username\":\"sensitive-password-marker\",\"password\":\"sensitive-password-marker\"}")]
    public async Task DeletedPasswordRoute_RejectsLikeUnknownAdminRoute(string body)
    {
        using var f = new Fixture();
        using var response = await f.Client.PostAsync("/admin/auth/login", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Authentication is required.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.TokenPosts);
        Assert.DoesNotContain(f.Logs.Messages, l => l.Contains("sensitive-password-marker", StringComparison.Ordinal));
        var handle = await f.Seed();
        // Without the antiforgery token the write fails the session boundary itself.
        using var csrf = await f.Send("/admin/auth/login", f.Cookie(handle), "POST", new StringContent(body));
        Assert.Equal(HttpStatusCode.Unauthorized, csrf.StatusCode);
        using var notFound = await f.Send("/admin/auth/login", f.Cookie(handle), "POST", new StringContent(body), csrf: true);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains("Admin endpoint was not found.", await notFound.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task StartSharesIpQuotaAndRetryAfter_LoginRouteIsNotMetered()
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["RateLimits:AdminLogin:Enabled"] = "true", ["RateLimits:AdminLogin:PermitLimit"] = "2", ["RateLimits:AdminLogin:WindowSeconds"] = "300" });
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.8");
        using var start = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        using var second = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        // The deleted password route is no longer part of the anonymous login surface.
        using var login = await f.Client.PostAsync("/admin/auth/login", new StringContent(""), Ct); Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        using var limited = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter); Assert.Equal("no-store", limited.Headers.CacheControl!.ToString());
        f.Client.DefaultRequestHeaders.Remove(VocabularyWebApplicationFactory.ClientAddressHeader);
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.9");
        using var other = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    // ASP.NET Core routing matches a literal route case-insensitively and with one
    // optional trailing slash, so every form below runs the same admin catch-all
    // and must carry the same guarantees as the canonical path. The method probe
    // was deleted with the password form; hosted login is the only sign-in.
    [Theory]
    [InlineData("/admin/auth/method")]
    [InlineData("/admin/auth/method/")]
    [InlineData("/ADMIN/AUTH/METHOD")]
    [InlineData("/Admin/Auth/Method/")]
    public async Task DeletedMethodRouteForms_AllBehaveAsUnknownAdminRoute(string path)
    {
        using var f = new Fixture();
        using var anonymous = await f.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("Authentication is required.", await anonymous.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.TokenPosts);
        var handle = await f.Seed();
        using var notFound = await f.Send(path, f.Cookie(handle));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains("Admin endpoint was not found.", await notFound.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/admin/auth/start")]
    [InlineData("/admin/auth/start/")]
    [InlineData("/ADMIN/AUTH/START")]
    [InlineData("/Admin/Auth/Start/")]
    public async Task StartRouteForms_CarryNoStoreAndCreateTransaction(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(2, response.Headers.GetValues("Set-Cookie").Count());
        Assert.Equal(0, f.TokenPosts);
    }

    [Theory]
    [InlineData("/admin/auth/callback")]
    [InlineData("/ADMIN/AUTH/CALLBACK")]
    public async Task CallbackRouteForms_CarrySafetyHeadersOnSuccessAndFailure(string path)
    {
        using var f = new Fixture();
        using var invalid = await f.Client.GetAsync(path + "?code=sensitive-code-marker", Ct);
        Failure(invalid, "sign_in_failed"); Assert.Equal(0, f.TokenPosts); SafetyHeaders(invalid);
        var canceled = await f.Start();
        using var cancel = await f.Send(f.Query(canceled, "access_denied", path), canceled.Cookie);
        Failure(cancel, "canceled"); SafetyHeaders(cancel);
        var success = await f.Start();
        using var signedIn = await f.Send(f.Query(success, path: path), success.Cookie);
        Assert.Equal("/#/books", signedIn.Headers.Location!.OriginalString);
        SafetyHeaders(signedIn);
        Assert.Equal(1, f.TokenPosts);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), s => s.StartsWith(success.Cookies[0].Split('=')[0] + "=;", StringComparison.Ordinal));
        f.AssertSafeLogs(success);
    }

    // Declared difference of the framework handler: the callback is intercepted by its exact
    // configured path, not by the router's trailing-slash-insensitive literal matching. The
    // non-canonical forms stay on the authenticated admin catch-all — the same fixed 401 and
    // security-header baseline as any unknown /admin route, with no redemption.
    [Theory]
    [InlineData("/admin/auth/callback/")]
    [InlineData("/Admin/Auth/Callback/")]
    public async Task TrailingSlashCallbackForms_KeepAdminCatchallBehavior(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync(path + "?code=sensitive-code-marker", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Authentication is required.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.TokenPosts);
    }

    // Restricted normalization must not claim paths routing itself rejects: a double
    // trailing slash or an extra segment stays on the authenticated admin catchall.
    [Theory]
    [InlineData("/admin/auth/login//")]
    [InlineData("/admin/auth/login/extra")]
    public async Task NonRouteForms_KeepAdminCatchallBehavior(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.PostAsync(path, new StringContent("{broken", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Authentication is required.", await response.Content.ReadAsStringAsync(Ct));
        using var method = await f.Client.GetAsync("/admin/auth/method//", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, method.StatusCode);
        // The catch-all is part of the /admin surface, so its answers carry the
        // shared security response-header baseline.
        Assert.Equal("no-store", method.Headers.CacheControl!.ToString());
        Assert.Equal(0, f.TokenPosts);
    }

    [Fact]
    public async Task StartRouteForms_ShareIpQuotaAndRetryAfter()
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["RateLimits:AdminLogin:Enabled"] = "true", ["RateLimits:AdminLogin:PermitLimit"] = "2", ["RateLimits:AdminLogin:WindowSeconds"] = "300" });
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.10");
        using var start = await f.Client.GetAsync("/admin/auth/start/", Ct); Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        using var second = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        using var limited = await f.Client.GetAsync("/ADMIN/AUTH/START", Ct); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter); Assert.Equal("no-store", limited.Headers.CacheControl!.ToString());
        f.Client.DefaultRequestHeaders.Remove(VocabularyWebApplicationFactory.ClientAddressHeader);
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.11");
        using var other = await f.Client.GetAsync("/admin/auth/start/", Ct); Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    [Theory]
    [InlineData("failure", 500, true)]
    [InlineData("busy", 503, true)]
    [InlineData("cancel", 0, true)]
    [InlineData("lost-commit", 500, true)]
    public async Task ActualStorageFaults_PreservePrecommitAndNeverRetryUnknownCommit(string mode, int status, bool oldSurvives)
    {
        var fault = new WriteFault(mode);
        using var f = new Fixture(interceptors: [fault, new CommitFault(fault, mode)]);
        var old = await f.Seed(); var t = await f.Start(); fault.Enabled = true;
        if (mode == "cancel")
        {
            // A cancelled storage write aborts the request's own leg: no session
            // cookie is delivered, no audit row exists, and the replay cannot
            // complete the consumed handshake.
            try
            {
                using var aborted = await f.Callback(t, f.Cookie(old));
                if (aborted.Headers.Contains("Set-Cookie"))
                    Assert.DoesNotContain(aborted.Headers.GetValues("Set-Cookie"),
                        c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
            }
            catch (OperationCanceledException)
            {
            }
            fault.Enabled = false;
            // The cancelled write never replaced anything: the old session survives.
            using var cancelled = await f.Send("/admin/auth/session", f.Cookie(old));
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            using var replayAfterCancel = await f.Callback(t); Failure(replayAfterCancel, "sign_in_failed");
            return;
        }
        using var response = await f.Callback(t, f.Cookie(old));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
        if (status == 503) Assert.NotNull(response.Headers.RetryAfter);
        fault.Enabled = false;
        using var existing = await f.Send("/admin/auth/session", f.Cookie(old));
        Assert.Equal(oldSurvives ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, existing.StatusCode);
        // The browser's carrier cookies were consumed with the failed attempt: an honest
        // replay presents no binding, never re-attempts the exchange, and the failed write is
        // never retried.
        using var replay = await f.Send(f.Query(t), t.Cookies[0].Split('=')[0] + "=");
        Failure(replay, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        using var scope = f.Host.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.CountAsync(Ct));
        Assert.DoesNotContain(f.Logs.Messages, l => l.Contains("synthetic-storage-secret-marker", StringComparison.Ordinal));
        f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task DisconnectDuringExchange_ConsumesWithoutChangingExistingSession()
    {
        using var f = new Fixture(); var old = await f.Seed(); var t = await f.Start();
        f.Authority.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, f.Query(t));
        request.Headers.Add("Cookie", t.Cookie + "; " + f.Cookie(old));
        var sending = f.Client.SendAsync(request, cancel.Token);
        await f.Authority.Entered.Task.WaitAsync(Ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        f.Authority.Gate.TrySetResult();
        using var existing = await f.Send("/admin/auth/session", f.Cookie(old));
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        // The browser's carrier cookies were consumed with the aborted attempt, and the code
        // was redeemed with it: an honest replay presents no binding and never re-attempts the
        // exchange.
        using var replay = await f.Send(f.Query(t), t.Cookies[0].Split('=')[0] + "=");
        Failure(replay, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task CodeMode_SourcePriorityCsrfPublicAndHealthRemainCompatible()
    {
        using var f = new Fixture(); var old = await f.Seed();
        var bearerStudent = f.Authority.Token("at+jwt", f.Authority.AccessClaims("student"));
        var legacyAdmin = f.Authority.Token("JWT", f.Authority.AccessClaims());
        var cookies = f.Cookie(old) + "; lexarborAdmin=" + legacyAdmin;
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session"); request.Headers.Add("Cookie", cookies);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerStudent);
        using var denied = await f.Client.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var invalidNew = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=bad; lexarborAdmin=" + legacyAdmin); Assert.Equal(HttpStatusCode.Unauthorized, invalidNew.StatusCode);
        // The session boundary keeps the CSRF rejection inside the session authentication
        // itself: an unsafe session-authenticated write without the antiforgery credential
        // answers the fixed 401, exactly like the replaced slice.
        using var csrf = await f.Send("/admin/vocabulary-books", cookies, "POST", JsonContent.Create(new { bookName = "CSRF", status = true })); Assert.Equal(HttpStatusCode.Unauthorized, csrf.StatusCode);
        Assert.Contains("Authentication is required.", await csrf.Content.ReadAsStringAsync(Ct));
        using var publicApi = await f.Client.GetAsync("/api/vocabulary-books/all", Ct); Assert.Equal(HttpStatusCode.OK, publicApi.StatusCode);
        using var health = await f.Client.GetAsync("/health", Ct); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        f.AssertSafeLogs(await f.Start());
    }

    private sealed class WriteFault(string mode) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("INSERT INTO admin_session", StringComparison.Ordinal))
            {
                if (mode == "cancel") throw new OperationCanceledException("synthetic-storage-secret-marker");
                if (mode is "failure" or "busy") throw new SqliteException("synthetic-storage-secret-marker", mode == "busy" ? 5 : 1);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CommitFault(WriteFault fault, string mode) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (fault.Enabled && mode == "lost-commit") throw new SqliteException("synthetic-storage-secret-marker", 1);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DefaultRegisteredLogging_DoesNotProjectCallbackOrTokenMaterial()
    {
        using var f = new Fixture(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
        });
        var t = await f.Start(); using var callback = await f.Callback(t);
        Assert.Equal("/#/books", callback.Headers.Location!.OriginalString); f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task CancelledCallback_NeverRedeemsAndTheBrowserMayStillCompleteOnce()
    {
        using var f = new Fixture(); var t = await f.Start();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try
        {
            await f.Host.Server.SendAsync(context =>
            {
                var uri = new Uri("https://lexarbor.test" + f.Query(t));
                context.Request.Method = "GET"; context.Request.Path = uri.AbsolutePath;
                context.Request.QueryString = new QueryString(uri.Query); context.Request.Headers.Cookie = t.Cookie;
                context.RequestAborted = cancel.Token;
            }, Ct);
        }
        catch (OperationCanceledException)
        {
            // The aborted request may end in its own cancellation or in the fixed failure
            // redirect; both leave no session behind.
        }
        // An already-aborted request no longer even starts the token call (the correlation
        // handler mounts ahead of the exchange client): zero posts and no session.
        Assert.Equal(0, f.TokenPosts);
        // Declared difference from the replaced slice: the framework consumes no server-side
        // state when the request aborts mid-flight, so the browser still holds its carrier
        // cookies and the authority's one-time code — the sign-in completes on the next
        // navigation, exactly once.
        using var completed = await f.Callback(t);
        Assert.Equal("/#/books", completed.Headers.Location!.OriginalString);
        Assert.Equal(1, f.TokenPosts);
        // The authority's one-time code (keyed to the handshake's verifier) refuses the
        // second redemption: every later attempt lands on the fixed failure.
        using var replay = await f.Callback(t);
        Failure(replay, "sign_in_failed");
        Assert.Equal(2, f.TokenPosts);
        f.AssertSafeLogs(t);
    }

    private static void Failure(HttpResponseMessage response, string reason)
    { Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/#/login?reason=" + reason, response.Headers.Location!.OriginalString); }

    private static void SafetyHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    private sealed record Transaction(string State, string Nonce, string Cookie, string[] Cookies, string[] SetCookies, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Parameters);
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
        public Fixture(Dictionary<string, string?>? extra = null, string provider = "OidcCode",
            IInterceptor[]? interceptors = null, bool expectStartupFailure = false)
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
                ["AdminAuthentication:OidcCode:PostLogoutRedirectUri"] = SignaCoreAuthorityStub.PostLogoutRedirect,
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
                    // The store re-verifies the access token through the JwtBearer options; the
                    // stub's RSA key signs those tokens, and the discovery metadata stays with the
                    // stub so the handler's backchannel speaks only to it.
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.Authority = null; options.MetadataAddress = null!; options.ConfigurationManager = null!;
                        options.TokenValidationParameters.IssuerSigningKey = Authority.SigningKey;
                    });
                    services.AddHttpClient(AdminAuthenticationSource.OidcHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => Authority);
                    if (interceptors is not null) services.AddDbContext<VocabularyDbContext>(o => o.AddInterceptors(interceptors));
                });
            });
            try
            {
                Client = Host.CreateClient(new() { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
                if (expectStartupFailure)
                {
                    using var failing = Client.GetAsync("/health/live", Ct).GetAwaiter().GetResult();
                    Assert.Fail("An illegal hosted-login configuration must fail host startup.");
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
        public async Task<Transaction> Start(string? target = "/books")
        {
            using var response = await Client.GetAsync("/admin/auth/start" + (target is null ? "" : "?returnUrl=" + Uri.EscapeDataString(target)), Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            var state = parameters["state"].ToString(); var nonce = parameters["nonce"].ToString();
            Authority.Challenges[parameters["code_challenge"].ToString()] = nonce;
            var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
            // Every carrier cookie of the handshake rides the callback request: the
            // framework's correlation and nonce cookies, both single-purpose.
            var cookies = setCookies.Select(cookie => cookie.Split(';')[0]).ToArray();
            return new(state, nonce, string.Join("; ", cookies), cookies, setCookies, parameters);
        }
        public string Query(Transaction t, string? error = null, string path = "/admin/auth/callback") => path + "?state=" + t.State + "&iss=" + Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer)
            + (error is null ? "&code=" + SignaCoreAuthorityStub.Code : "&error=" + error);
        public Task<HttpResponseMessage> Callback(Transaction t, string extraCookie = "") => Send(Query(t), t.Cookie + (extraCookie.Length == 0 ? "" : "; " + extraCookie));
        public Task<HttpResponseMessage> Send(string path, string cookie = "", string method = "GET", HttpContent? body = null, bool csrf = false, string? correlationId = null)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
            if (correlationId is not null) request.Headers.Add("x-correlation-id", correlationId);
            var cookies = new List<string>();
            if (cookie.Length > 0) cookies.Add(cookie);
            if (csrf)
            {
                if (_csrfPair is null) _csrfPair = FetchCsrf();
                request.Headers.Add(AdminTestAntiforgery.HeaderName, _csrfPair.Value.Token);
                cookies.Add(_csrfPair.Value.Cookie);
            }
            if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies));
            return Client.SendAsync(request, Ct);
        }
        private (string Token, string Cookie)? _csrfPair;
        private (string Token, string Cookie) FetchCsrf()
        {
            using var response = Client.GetAsync("/admin/auth/csrf", Ct).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(response.Content.ReadAsStream(Ct)).RootElement.GetProperty("token").GetString()!;
            return (token, response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        }
        public async Task<string> Seed()
        {
            using var scope = Host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
            { AccessToken = "synthetic-old-access-marker", IdToken = "synthetic-old-id-marker", Issuer = SignaCoreAuthorityStub.Issuer, Subject = "old-sub", DisplayName = "old-user", Roles = ["admin"], AccessTokenExpiresAt = Clock.Now.AddMinutes(15) }, Ct);
        }
        /// <summary>The session-cookie value a signed-in browser would hold for a stored handle.</summary>
        public string Cookie(string handle) => AdminSessionTestCookie.For(Host.Services, handle);
        public int TokenPosts => Authority.TokenPosts;
        public Dictionary<string, string> TokenForm => Authority.TokenForm;
        public string LastAccess => Authority.LastAccess;
        public string LastId => Authority.LastId;
        public void AssertSafeLogs(Transaction t)
        {
            // The correlation cookie's random name suffix and the nonce cookie's protected
            // value are the handshake secrets a log line must never carry.
            var correlationSecret = t.Cookies[0][".AspNetCore.Correlation.".Length..].Split('=')[0];
            var nonceSecret = t.Cookies.Length > 1 ? t.Cookies[1].Split('=')[1] : "";
            foreach (var marker in new[] { SignaCoreAuthorityStub.Code, SignaCoreAuthorityStub.Secret, LastAccess, LastId, Authority.TokenForm.GetValueOrDefault("code_verifier", ""), correlationSecret, nonceSecret, "sensitive-description-marker", "sensitive-code-marker", "sensitive-unknown-error-marker", "synthetic-old-access-marker", "synthetic-old-id-marker", "?state=" })
                if (marker.Length > 1) Assert.DoesNotContain(Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
        }
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

/// <summary>
/// Test-side reads of the opaque session cookie: unwraps the data-protected ticket the cookie
/// handler issues back into the server-side handle it names, for assertions that correlate a
/// presented cookie with the stored row.
/// </summary>
public static class AdminSessionTestCookieReads
{
    public static string HandleOf(IServiceProvider services, string cookie)
    {
        var value = cookie.Split(';', 2)[0].Split('=', 2)[1];
        var options = services.GetRequiredService<
            Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(AdminAuthenticationSource.SessionScheme);
        var ticket = options.TicketDataFormat.Unprotect(value);
        Assert.NotNull(ticket);
        return ticket!.Principal.FindFirst(AdminSessionTestCookie.SessionIdClaim)!.Value;
    }
}
