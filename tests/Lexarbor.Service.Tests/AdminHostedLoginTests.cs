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
using SignaCore.Client.AspNetCore;

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
        Assert.DoesNotContain("response_mode", t.Parameters.Keys);
        Assert.Contains("secure", t.SetCookie); Assert.Contains("httponly", t.SetCookie); Assert.Contains("samesite=lax", t.SetCookie);
        Assert.Contains("path=/admin/auth/callback", t.SetCookie); Assert.DoesNotContain("domain=", t.SetCookie);
        using var callback = await f.Send(f.Query(t) + "&returnUrl=https%3A%2F%2Fevil.test%2Fignored", t.Cookie);
        Assert.Equal("/#/books/book_1/words", callback.Headers.Location!.OriginalString);
        Assert.Equal("no-store", callback.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", callback.Headers.GetValues("Referrer-Policy").Single());
        var cookie = callback.Headers.GetValues("Set-Cookie").Single(s => s.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        Assert.DoesNotContain(f.LastAccess, cookie); Assert.DoesNotContain(f.LastId, cookie);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), s => s.StartsWith(t.Cookie.Split('=')[0] + "=;", StringComparison.Ordinal));
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
        // client_secret_basic: the credentials never enter the form, only the Basic header.
        Assert.Equal(4, f.TokenForm.Count);
        Assert.DoesNotContain(f.TokenForm, pair => pair.Key.Contains("client", StringComparison.Ordinal));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "client-id:" + SignaCoreAuthorityStub.Secret)), f.Authority.LastAuthorizationHeader);
        Assert.Equal(t.Parameters["code_challenge"], WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(f.TokenForm["code_verifier"]))));
        using var scope = f.Host.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.SingleAsync(Ct);
        Assert.DoesNotContain(f.LastAccess, row.ProtectedPayload); Assert.DoesNotContain(f.LastId, row.ProtectedPayload);
        var saved = await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().ReadAsync(cookie.Split('=')[1], Ct);
        Assert.Equal(f.LastId, saved!.IdToken); Assert.Equal(f.LastAccess, saved.AccessToken);
        f.AssertSafeLogs(t);
    }

    [Theory]
    [InlineData(null, 302)]
    [InlineData("/books", 302)]
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
    [InlineData("http://lexarbor.test/admin/auth/callback")]
    public async Task ConfiguredButIllegalRedirectUri_FailsStartup(string redirect)
    {
        // A configured but illegal protocol option is a startup failure — the official
        // validator's missing-versus-illegal split behind AllowUnconfiguredStartup.
        using var f = new Fixture(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:RedirectUri"] = redirect }, expectStartupFailure: true);
        Assert.True(f.StartupFailed);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("browser")]
    [InlineData("issuer")]
    [InlineData("duplicate-state")]
    [InlineData("duplicate-code")]
    [InlineData("duplicate-issuer")]
    [InlineData("both")]
    [InlineData("no-result")]
    [InlineData("malformed-code")]
    [InlineData("unknown-error")]
    [InlineData("duplicate-error")]
    [InlineData("expired")]
    public async Task InvalidCallback_NeverRedeemsOrDeletesAnotherTransaction(string defect)
    {
        using var f = new Fixture(); var t = await f.Start();
        var query = f.Query(t);
        var cookie = t.Cookie;
        switch (defect)
        {
            case "state": query = query.Replace(t.State, SignaCoreAuthorityStub.Code, StringComparison.Ordinal); break;
            case "browser": cookie = cookie.Split('=')[0] + "=" + SignaCoreAuthorityStub.Code; break;
            case "issuer": query = query.Replace(Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer), Uri.EscapeDataString("https://evil.test"), StringComparison.Ordinal); break;
            case "duplicate-state": query += "&state=" + t.State; break;
            case "duplicate-code": query += "&code=" + SignaCoreAuthorityStub.Code; break;
            case "duplicate-issuer": query += "&iss=" + Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer); break;
            case "both": query += "&error=access_denied"; break;
            case "no-result": query = query.Replace("&code=" + SignaCoreAuthorityStub.Code, "", StringComparison.Ordinal); break;
            case "malformed-code": query = query.Replace(SignaCoreAuthorityStub.Code, "sensitive-code-marker", StringComparison.Ordinal); break;
            case "unknown-error": query = f.Query(t, "sensitive-unknown-error-marker"); break;
            case "duplicate-error": query = f.Query(t, "access_denied") + "&error=access_denied"; break;
            case "expired": f.Clock.Now += TimeSpan.FromMinutes(5); break;
        }
        using var response = await f.Send(query, cookie);
        Failure(response, defect == "both" ? "canceled" : "sign_in_failed");
        // A rejection before the binding-cookie deletion carries no Set-Cookie at all;
        // every later shape deletes only the transaction's own binding cookie.
        if (response.Headers.Contains("Set-Cookie"))
            Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
        // The official consumption semantics: a callback that reaches the state
        // consumption or the token redemption finishes the pending sign-in for this
        // browser (its binding cookie was deleted with the response either way); a
        // response rejected before that point — a duplicated parameter or an unknown
        // state — leaves the handshake alive for the browser that owns it.
        var consumes = defect is "expired" or "browser" or "issuer" or "malformed-code" or "both" or "unknown-error" or "duplicate-error";
        if (!consumes)
        {
            using var valid = await f.Callback(t);
            Assert.Equal("/#/books", valid.Headers.Location!.OriginalString);
        }
        else
        {
            // The browser's binding cookie was deleted with the rejection, so its
            // replay presents no binding and cannot complete the handshake.
            using var replay = await f.Send(f.Query(t), t.Cookie.Split('=')[0] + "=");
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
        Assert.Single(response.Headers.GetValues("Set-Cookie"));
        // The browser's binding cookie was deleted with the response, so its own
        // replay cannot complete the consumed handshake; the other browser's
        // transaction is untouched and independent.
        using var replay = await f.Send(f.Query(t), t.Cookie.Split('=')[0] + "=");
        Failure(replay, "sign_in_failed"); Assert.Equal(0, f.TokenPosts);
        using var independent = await f.Callback(other); Assert.Equal("/#/books", independent.Headers.Location!.OriginalString);
        f.AssertSafeLogs(t);
    }

    [Theory]
    [InlineData("nonadmin", "denied")]
    [InlineData("id-role", "denied")]
    [InlineData("nonce", "sign_in_failed")]
    [InlineData("sub", "sign_in_failed")]
    [InlineData("network", "provider_unavailable")]
    [InlineData("timeout", "provider_unavailable")]
    [InlineData("307", "sign_in_failed")]
    [InlineData("308", "sign_in_failed")]
    [InlineData("missing-id", "sign_in_failed")]
    [InlineData("missing-access", "sign_in_failed")]
    [InlineData("malformed", "sign_in_failed")]
    [InlineData("invalid-grant", "sign_in_failed")]
    [InlineData("token-type", "sign_in_failed")]
    [InlineData("expires", "sign_in_failed")]
    [InlineData("scope", "sign_in_failed")]
    [InlineData("oversize-token", "sign_in_failed")]
    [InlineData("oversize-body", "sign_in_failed")]
    [InlineData("bad-token", "sign_in_failed")]
    public async Task FailedCallback_PreservesBothExistingSessionKinds(string defect, string reason)
    {
        using var f = new Fixture();
        var old = await f.Seed(); var legacy = f.Authority.Token("JWT", f.Authority.AccessClaims("admin"));
        var t = await f.Start(); f.Authority.TokenDefect = defect;
        using var response = await f.Callback(t, AdminSessionCookie.Name + "=" + old + "; lexarborAdmin=" + legacy);
        Failure(response, reason);
        Assert.All(response.Headers.GetValues("Set-Cookie"), c => Assert.StartsWith(t.Cookie.Split('=')[0] + "=", c));
        using var existing = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + old); Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        // The legacy password-login JWT cookie authenticates nothing anymore.
        using var oldSession = await f.Send("/admin/auth/session", "lexarborAdmin=" + legacy); Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var replay = await f.Callback(t); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        f.AssertSafeLogs(t);
    }

    [Theory]
    [InlineData("id", "typ")]
    [InlineData("access", "typ")]
    [InlineData("id", "aud")]
    [InlineData("access", "aud")]
    [InlineData("id", "iss")]
    [InlineData("access", "iss")]
    [InlineData("id", "signature")]
    [InlineData("access", "signature")]
    [InlineData("id", "exp")]
    [InlineData("access", "exp")]
    [InlineData("id", "iat")]
    [InlineData("access", "iat")]
    [InlineData("access", "nbf")]
    [InlineData("access", "kid")]
    [InlineData("id", "missing-sub")]
    [InlineData("access", "missing-sub")]
    [InlineData("access", "duplicate-sub")]
    [InlineData("id", "alg")]
    [InlineData("access", "alg")]
    [InlineData("id", "empty-sub")]
    [InlineData("access", "empty-sub")]
    [InlineData("access", "missing-nbf")]
    [InlineData("id", "missing-exp")]
    [InlineData("access", "duplicate-exp")]
    [InlineData("id", "missing-nonce")]
    public async Task BothRealJwtTrustPaths_MustPassBeforeSignIn(string kind, string defect)
    {
        using var f = new Fixture(); var t = await f.Start(); f.Authority.TokenDefect = kind + ":" + defect;
        using var response = await f.Callback(t); Failure(response, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        using var scope = f.Host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
        f.AssertSafeLogs(t);
    }


    // The official validator's tolerated shapes: a JWT without an explicit iat, a
    // key selected without kid (the single published key), a multi-valued audience
    // containing the client, and repeated members inside a compact JWT — the package
    // validates the signature, issuer, audience, lifetime, nonce and subject and
    // signs these in, where the retired implementation rejected them outright.
    [Theory]
    [InlineData("id", "kid")]
    [InlineData("id", "missing-iat")]
    [InlineData("id", "duplicate-iat")]
    [InlineData("id", "duplicate-nonce")]
    [InlineData("id", "duplicate-sub")]
    [InlineData("id", "extra-aud")]
    public async Task OfficiallyToleratedJwtShapes_StillSignIn(string kind, string defect)
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
        using var replay = await f.Callback(first); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        var two = f.Callback(second);
        while (f.TokenPosts < 2) await Task.Delay(10, Ct);
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
        // Without the antiforgery token the write fails the session authentication itself.
        using var csrf = await f.Send("/admin/auth/login", AdminSessionCookie.Name + "=" + handle, "POST", new StringContent(body));
        Assert.Equal(HttpStatusCode.Unauthorized, csrf.StatusCode);
        using var notFound = await f.Send("/admin/auth/login", AdminSessionCookie.Name + "=" + handle, "POST", new StringContent(body), csrf: true);
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
        using var notFound = await f.Send(path, AdminSessionCookie.Name + "=" + handle);
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
        Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Equal(0, f.TokenPosts);
    }

    [Theory]
    [InlineData("/admin/auth/callback")]
    [InlineData("/admin/auth/callback/")]
    [InlineData("/ADMIN/AUTH/CALLBACK")]
    [InlineData("/Admin/Auth/Callback/")]
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
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), s => s.StartsWith(success.Cookie.Split('=')[0] + "=;", StringComparison.Ordinal));
        f.AssertSafeLogs(success);
    }

    // ASP.NET Core routing matches a literal route case-insensitively and with one
    // optional trailing slash, so every form below runs the same admin catch-all
    // and must carry the same guarantees as the canonical path.
    [Theory]
    [InlineData("/admin/auth/login")]
    [InlineData("/admin/auth/login/")]
    [InlineData("/ADMIN/AUTH/LOGIN")]
    [InlineData("/Admin/Auth/Login/")]
    public async Task DeletedLoginRouteForms_AllBehaveAsUnknownAdminRoute(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.PostAsync(path, new StringContent("{broken", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Authentication is required.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.TokenPosts);
        Assert.DoesNotContain(f.Logs.Messages, l => l.Contains("sensitive-password-marker", StringComparison.Ordinal));
        var handle = await f.Seed();
        using var csrf = await f.Send(path, AdminSessionCookie.Name + "=" + handle, "POST", new StringContent("{broken"));
        Assert.Equal(HttpStatusCode.Unauthorized, csrf.StatusCode);
        using var notFound = await f.Send(path, AdminSessionCookie.Name + "=" + handle, "POST", new StringContent("{broken"), csrf: true);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
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
    [InlineData("lost-commit", 500, false)]
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
                using var aborted = await f.Callback(t, AdminSessionCookie.Name + "=" + old);
                if (aborted.Headers.Contains("Set-Cookie"))
                    Assert.DoesNotContain(aborted.Headers.GetValues("Set-Cookie"),
                        c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
            }
            catch (OperationCanceledException)
            {
            }
            fault.Enabled = false;
            // The cancelled write never replaced anything: the old session survives.
            using var cancelled = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + old);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            using var replayAfterCancel = await f.Callback(t); Failure(replayAfterCancel, "sign_in_failed");
            return;
        }
        using var response = await f.Callback(t, AdminSessionCookie.Name + "=" + old);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
        if (status == 503) Assert.NotNull(response.Headers.RetryAfter);
        fault.Enabled = false;
        using var existing = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + old);
        Assert.Equal(oldSurvives ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, existing.StatusCode);
        using var replay = await f.Callback(t); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
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
        request.Headers.Add("Cookie", t.Cookie + "; " + AdminSessionCookie.Name + "=" + old);
        var sending = f.Client.SendAsync(request, cancel.Token);
        await f.Authority.Entered.Task.WaitAsync(Ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        f.Authority.Gate.TrySetResult();
        using var existing = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + old);
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        using var replay = await f.Callback(t); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.TokenPosts);
        f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task CodeMode_SourcePriorityCsrfPublicAndHealthRemainCompatible()
    {
        using var f = new Fixture(); var old = await f.Seed();
        var bearerStudent = f.Authority.Token("at+jwt", f.Authority.AccessClaims("student"));
        var legacyAdmin = f.Authority.Token("JWT", f.Authority.AccessClaims());
        var cookies = AdminSessionCookie.Name + "=" + old + "; lexarborAdmin=" + legacyAdmin;
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session"); request.Headers.Add("Cookie", cookies);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerStudent);
        using var denied = await f.Client.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var invalidNew = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=bad; lexarborAdmin=" + legacyAdmin); Assert.Equal(HttpStatusCode.Unauthorized, invalidNew.StatusCode);
        using var csrf = await f.Send("/admin/vocabulary-books", cookies, "POST", JsonContent.Create(new { bookName = "CSRF", status = true })); Assert.Equal(HttpStatusCode.Unauthorized, csrf.StatusCode);
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
    public async Task CancelledCallback_ConsumesItsPendingOnceAndNeverRedeems()
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
            // The aborted request ends in its own cancellation.
        }
        // The cancelled request reached the token endpoint at most once and consumed
        // the pending sign-in on its way: the official one-transaction semantics.
        Assert.Equal(1, f.TokenPosts);
        using var replay = await f.Callback(t);
        Failure(replay, "sign_in_failed");
        Assert.Equal(1, f.TokenPosts);
        f.AssertSafeLogs(t);
    }

    private static void Failure(HttpResponseMessage response, string reason)
    { Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/#/login?reason=" + reason, response.Headers.Location!.OriginalString); }

    private static void SafetyHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    private sealed record Transaction(string State, string Nonce, string Cookie, string SetCookie, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Parameters);
    private sealed class Fixture : IDisposable
    {
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client { get; }
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
                    // stub so the official package's backchannel speaks only to it.
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.Authority = null; options.MetadataAddress = null!; options.ConfigurationManager = null!;
                        options.TokenValidationParameters.IssuerSigningKey = Authority.SigningKey;
                    });
                    services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
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
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            return new(state, nonce, cookie.Split(';')[0], cookie, parameters);
        }
        public string Query(Transaction t, string? error = null, string path = "/admin/auth/callback") => path + "?state=" + t.State + "&iss=" + Uri.EscapeDataString(SignaCoreAuthorityStub.Issuer)
            + (error is null ? "&code=" + SignaCoreAuthorityStub.Code : "&error=" + error);
        public Task<HttpResponseMessage> Callback(Transaction t, string extraCookie = "") => Send(Query(t), t.Cookie + (extraCookie.Length == 0 ? "" : "; " + extraCookie));
        public Task<HttpResponseMessage> Send(string path, string cookie = "", string method = "GET", HttpContent? body = null, bool csrf = false)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
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
        public int TokenPosts => Authority.TokenPosts;
        public Dictionary<string, string> TokenForm => Authority.TokenForm;
        public string LastAccess => Authority.LastAccess;
        public string LastId => Authority.LastId;
        public void AssertSafeLogs(Transaction t)
        {
            foreach (var marker in new[] { SignaCoreAuthorityStub.Code, SignaCoreAuthorityStub.Secret, LastAccess, LastId, Authority.TokenForm.GetValueOrDefault("code_verifier", ""), t.Cookie.Split('=')[1], "sensitive-description-marker", "sensitive-code-marker", "sensitive-unknown-error-marker", "synthetic-old-access-marker", "synthetic-old-id-marker", "?state=" })
                if (marker.Length > 0) Assert.DoesNotContain(Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
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
