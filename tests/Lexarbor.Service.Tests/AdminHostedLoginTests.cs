using System.Collections.Concurrent;
using System.Data.Common;
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
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

public class AdminHostedLoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealCodeFlow_UsesExactFormEncryptedSessionAndAdminReadWrite()
    {
        using var f = new Fixture();
        using var method = await f.Client.GetAsync("/admin/auth/method", Ct);
        Assert.Equal("{\"success\":true,\"data\":{\"method\":\"hosted\"}}", await method.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", method.Headers.CacheControl!.ToString());
        var t = await f.Start("/books/book_1/words");
        Assert.Equal(8, t.Parameters.Count);
        Assert.All(t.Parameters, pair => Assert.Single(pair.Value));
        Assert.Equal("code", t.Parameters["response_type"]);
        Assert.Equal(Fixture.Redirect, t.Parameters["redirect_uri"]);
        Assert.Equal("openid profile", t.Parameters["scope"]);
        Assert.Equal("S256", t.Parameters["code_challenge_method"]);
        Assert.DoesNotContain("response_mode", t.Parameters.Keys);
        Assert.Contains("secure", t.SetCookie); Assert.Contains("httponly", t.SetCookie); Assert.Contains("samesite=lax", t.SetCookie);
        Assert.Contains("path=/", t.SetCookie); Assert.Contains("max-age=300", t.SetCookie); Assert.DoesNotContain("domain=", t.SetCookie);
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
        Assert.Equal(1, f.Posts);
        Assert.Equal(Fixture.Code, f.Form["code"]);
        Assert.Equal("authorization_code", f.Form["grant_type"]);
        Assert.Equal(Fixture.Redirect, f.Form["redirect_uri"]);
        Assert.Equal(Fixture.Secret, f.Form["client_secret"]);
        Assert.Equal(t.Parameters["code_challenge"], WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(f.Form["code_verifier"]))));
        Assert.Equal(6, f.Form.Count);
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
    [InlineData("reserved", 503)]
    [InlineData("duplicate-static", 503)]
    [InlineData("discovery", 502)]
    [InlineData("untrusted", 502)]
    [InlineData("full", 503)]
    [InlineData("duplicate-return", 400)]
    public async Task Start_FailsWithoutCorrelationCookieOrSession(string failure, int status)
    {
        var config = new Dictionary<string, string?>();
        if (failure == "config") config["AdminAuthentication:OidcCode:ClientSecret"] = "";
        if (failure == "reserved") config["AdminAuthentication:OidcCode:RedirectUri"] = "https://lexarbor.test/admin/auth/callback?state=bad";
        if (failure == "duplicate-static") config["AdminAuthentication:OidcCode:RedirectUri"] = "https://lexarbor.test/admin/auth/callback?x=1&x=2";
        using var f = new Fixture(config);
        f.Manager.Fail = failure == "discovery";
        if (failure == "untrusted") f.Manager.Configuration.AuthorizationEndpoint = "https://evil.test/login";
        if (failure == "full") for (var i = 0; i < PendingAdminLoginStore.Capacity; i++) Assert.NotNull(f.Host.Services.GetRequiredService<PendingAdminLoginStore>().Create(cancellationToken: Ct));
        using var response = await f.Client.GetAsync("/admin/auth/start" + (failure == "duplicate-return" ? "?returnUrl=/books&returnUrl=/books" : ""), Ct);
        Assert.Equal(status, (int)response.StatusCode); Assert.False(response.Headers.Contains("Set-Cookie")); Assert.Equal(0, f.Posts);
        Assert.DoesNotContain(Fixture.Secret, await response.Content.ReadAsStringAsync(Ct));
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
            case "state": query = query.Replace(t.State, Fixture.Code, StringComparison.Ordinal); break;
            case "browser": cookie = cookie.Split('=')[0] + "=" + Fixture.Code; break;
            case "issuer": query = query.Replace(Uri.EscapeDataString(Fixture.Issuer), Uri.EscapeDataString("https://evil.test"), StringComparison.Ordinal); break;
            case "duplicate-state": query += "&state=" + t.State; break;
            case "duplicate-code": query += "&code=" + Fixture.Code; break;
            case "duplicate-issuer": query += "&iss=" + Uri.EscapeDataString(Fixture.Issuer); break;
            case "both": query += "&error=access_denied"; break;
            case "no-result": query = query.Replace("&code=" + Fixture.Code, "", StringComparison.Ordinal); break;
            case "malformed-code": query = query.Replace(Fixture.Code, "sensitive-code-marker", StringComparison.Ordinal); break;
            case "unknown-error": query = f.Query(t, "sensitive-unknown-error-marker"); break;
            case "duplicate-error": query = f.Query(t, "access_denied") + "&error=access_denied"; break;
            case "expired": f.Clock.Now += TimeSpan.FromMinutes(5); break;
        }
        using var response = await f.Send(query, cookie);
        Failure(response, "sign_in_failed"); Assert.False(response.Headers.Contains("Set-Cookie")); Assert.Equal(0, f.Posts);
        if (defect != "expired") { using var valid = await f.Callback(t); Assert.Equal("/#/books", valid.Headers.Location!.OriginalString); }
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
        Failure(response, reason); Assert.Equal(0, f.Posts);
        Assert.Single(response.Headers.GetValues("Set-Cookie"));
        using var replay = await f.Callback(t); Failure(replay, "sign_in_failed"); Assert.Equal(0, f.Posts);
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
    [InlineData("307", "provider_unavailable")]
    [InlineData("308", "provider_unavailable")]
    [InlineData("missing-id", "sign_in_failed")]
    [InlineData("missing-access", "sign_in_failed")]
    [InlineData("malformed", "provider_unavailable")]
    [InlineData("invalid-grant", "sign_in_failed")]
    [InlineData("token-type", "sign_in_failed")]
    [InlineData("expires", "sign_in_failed")]
    [InlineData("scope", "sign_in_failed")]
    [InlineData("oversize-token", "sign_in_failed")]
    [InlineData("oversize-body", "provider_unavailable")]
    [InlineData("bad-token", "provider_unavailable")]
    public async Task FailedCallback_PreservesBothExistingSessionKinds(string defect, string reason)
    {
        using var f = new Fixture();
        var old = await f.Seed(); var legacy = f.Token("JWT", f.Claims("admin"));
        var t = await f.Start(); f.Defect = defect;
        using var response = await f.Callback(t, AdminSessionCookie.Name + "=" + old + "; lexarborAdmin=" + legacy);
        Failure(response, reason);
        Assert.All(response.Headers.GetValues("Set-Cookie"), c => Assert.StartsWith(t.Cookie.Split('=')[0] + "=", c));
        using var existing = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + old); Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        using var oldSession = await f.Send("/admin/auth/session", "lexarborAdmin=" + legacy); Assert.Equal(HttpStatusCode.OK, oldSession.StatusCode);
        using var replay = await f.Callback(t); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.Posts);
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
    [InlineData("id", "kid")]
    [InlineData("access", "kid")]
    [InlineData("id", "missing-sub")]
    [InlineData("access", "missing-sub")]
    [InlineData("id", "duplicate-sub")]
    [InlineData("access", "duplicate-sub")]
    [InlineData("id", "alg")]
    [InlineData("access", "alg")]
    [InlineData("id", "extra-aud")]
    [InlineData("access", "extra-aud")]
    [InlineData("id", "empty-sub")]
    [InlineData("access", "empty-sub")]
    [InlineData("id", "missing-iat")]
    [InlineData("access", "missing-iat")]
    [InlineData("id", "missing-exp")]
    [InlineData("access", "missing-exp")]
    [InlineData("id", "duplicate-iat")]
    [InlineData("access", "duplicate-iat")]
    [InlineData("id", "duplicate-exp")]
    [InlineData("access", "duplicate-exp")]
    [InlineData("id", "missing-nonce")]
    [InlineData("id", "duplicate-nonce")]
    public async Task BothRealJwtTrustPaths_MustPassBeforeSignIn(string kind, string defect)
    {
        using var f = new Fixture(); var t = await f.Start(); f.Defect = kind + ":" + defect;
        using var response = await f.Callback(t); Failure(response, "sign_in_failed"); Assert.Equal(1, f.Posts);
        using var scope = f.Host.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
        f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task ConcurrentCallbacks_OnePostPerTransactionAndIndependentSignIns()
    {
        using var f = new Fixture();
        var starts = await Task.WhenAll(f.Start(), f.Start()); var first = starts[0]; var second = starts[1];
        Assert.NotEqual(first.State, second.State); Assert.NotEqual(first.Cookie, second.Cookie);
        f.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = f.Callback(first); await f.Entered.Task.WaitAsync(Ct);
        using var replay = await f.Callback(first); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.Posts);
        var two = f.Callback(second);
        while (f.Posts < 2) await Task.Delay(10, Ct);
        f.Gate.SetResult(); using var r1 = await one; using var r2 = await two;
        Assert.Equal("/#/books", r1.Headers.Location!.OriginalString); Assert.Equal("/#/books", r2.Headers.Location!.OriginalString);
        using var scope = f.Host.Services.CreateScope(); Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.CountAsync(Ct));
        Assert.NotEqual(r1.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)), r2.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("")]
    [InlineData("{\"username\":\"sensitive-password-marker\",\"password\":\"sensitive-password-marker\"}")]
    public async Task CodePasswordEntry_RejectsBeforeBindingOrReadingBody(string body)
    {
        using var f = new Fixture();
        using var response = await f.Client.PostAsync("/admin/auth/login", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"success\":false,\"message\":\"Password login is disabled for hosted authentication.\"}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.Posts);
        Assert.DoesNotContain(f.Logs.Messages, l => l.Contains("sensitive-password-marker", StringComparison.Ordinal));
        var handle = await f.Seed();
        using var csrf = await f.Send("/admin/auth/login", AdminSessionCookie.Name + "=" + handle, "POST", new StringContent(body));
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
    }

    [Fact]
    public async Task StartAndPassword_ShareIpQuotaAndRetryAfter()
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["RateLimits:AdminLogin:Enabled"] = "true", ["RateLimits:AdminLogin:PermitLimit"] = "2", ["RateLimits:AdminLogin:WindowSeconds"] = "300" });
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.8");
        using var start = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        using var password = await f.Client.PostAsync("/admin/auth/login", new StringContent(""), Ct); Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode);
        using var limited = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter); Assert.Equal("no-store", limited.Headers.CacheControl!.ToString());
        f.Client.DefaultRequestHeaders.Remove(VocabularyWebApplicationFactory.ClientAddressHeader);
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.9");
        using var other = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    // ASP.NET Core routing matches a literal route case-insensitively and with one
    // optional trailing slash, so every form below runs the same endpoint and must
    // carry the same guarantees as the canonical path.
    [Theory]
    [InlineData("/admin/auth/method")]
    [InlineData("/admin/auth/method/")]
    [InlineData("/ADMIN/AUTH/METHOD")]
    [InlineData("/Admin/Auth/Method/")]
    public async Task MethodRouteForms_CarryNoStoreOnEveryAcceptedForm(string path)
    {
        using var f = new Fixture();
        using var response = await f.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"method\":\"hosted\"", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
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
        Assert.Equal(0, f.Posts);
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
        Failure(invalid, "sign_in_failed"); Assert.Equal(0, f.Posts); SafetyHeaders(invalid);
        var canceled = await f.Start();
        using var cancel = await f.Send(f.Query(canceled, "access_denied", path), canceled.Cookie);
        Failure(cancel, "canceled"); SafetyHeaders(cancel);
        var success = await f.Start();
        using var signedIn = await f.Send(f.Query(success, path: path), success.Cookie);
        Assert.Equal("/#/books", signedIn.Headers.Location!.OriginalString);
        SafetyHeaders(signedIn);
        Assert.Equal(1, f.Posts);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), s => s.StartsWith(success.Cookie.Split('=')[0] + "=;", StringComparison.Ordinal));
        f.AssertSafeLogs(success);
    }

    [Theory]
    [InlineData("/admin/auth/login", "application/json", "{broken")]
    [InlineData("/admin/auth/login/", "application/json", "{broken")]
    [InlineData("/ADMIN/AUTH/LOGIN", "application/json", "{broken")]
    [InlineData("/Admin/Auth/Login/", "application/json", "{broken")]
    [InlineData("/admin/auth/login", "text/plain", "{broken")]
    [InlineData("/admin/auth/login/", "text/plain", "{broken")]
    [InlineData("/ADMIN/AUTH/LOGIN/", "text/plain", "{broken")]
    [InlineData("/admin/auth/login/", "application/json", "")]
    [InlineData("/Admin/Auth/Login/", "application/json", "")]
    [InlineData("/admin/auth/login/", "application/json", "{\"username\":\"sensitive-password-marker\",\"password\":\"sensitive-password-marker\"}")]
    [InlineData("/ADMIN/AUTH/LOGIN/", "application/json", "{\"username\":\"sensitive-password-marker\",\"password\":\"sensitive-password-marker\"}")]
    [InlineData("/Admin/Auth/Login/", "text/plain", "{\"username\":\"sensitive-password-marker\",\"password\":\"sensitive-password-marker\"}")]
    public async Task CodePasswordEntry_RejectsEveryAcceptedRouteFormBeforeBinding(string path, string contentType, string body)
    {
        using var f = new Fixture();
        using var response = await f.Client.PostAsync(path, new StringContent(body, Encoding.UTF8, contentType), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"success\":false,\"message\":\"Password login is disabled for hosted authentication.\"}", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, f.Posts);
        Assert.DoesNotContain(f.Logs.Messages, l => l.Contains("sensitive-password-marker", StringComparison.Ordinal));
        var handle = await f.Seed();
        using var csrf = await f.Send(path, AdminSessionCookie.Name + "=" + handle, "POST", new StringContent(body));
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
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
        Assert.False(method.Headers.Contains("Cache-Control"));
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task StartAndLoginRouteForms_ShareIpQuotaAndRetryAfter()
    {
        using var f = new Fixture(new Dictionary<string, string?> { ["RateLimits:AdminLogin:Enabled"] = "true", ["RateLimits:AdminLogin:PermitLimit"] = "2", ["RateLimits:AdminLogin:WindowSeconds"] = "300" });
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.10");
        using var start = await f.Client.GetAsync("/admin/auth/start/", Ct); Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        using var password = await f.Client.PostAsync("/Admin/Auth/Login/", new StringContent(""), Ct); Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode);
        using var limited = await f.Client.GetAsync("/ADMIN/AUTH/START", Ct); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter); Assert.Equal("no-store", limited.Headers.CacheControl!.ToString());
        f.Client.DefaultRequestHeaders.Remove(VocabularyWebApplicationFactory.ClientAddressHeader);
        f.Client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.11");
        using var other = await f.Client.GetAsync("/admin/auth/start/", Ct); Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    [Theory]
    [InlineData("Oidc")]
    [InlineData("Gateway")]
    public async Task OldModes_KeepPasswordMethodAndRefuseHostedRoutes(string provider)
    {
        using var f = new Fixture(provider: provider);
        using var method = await f.Client.GetAsync("/admin/auth/method", Ct); Assert.Contains("password", await method.Content.ReadAsStringAsync(Ct));
        using var start = await f.Client.GetAsync("/admin/auth/start", Ct); Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode); Assert.False(start.Headers.Contains("Set-Cookie"));
        using var callback = await f.Client.GetAsync("/admin/auth/callback?code=sensitive-code-marker", Ct); Failure(callback, "sign_in_failed"); Assert.False(callback.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("Oidc")]
    [InlineData("Gateway")]
    public async Task OldModes_RouteFormsKeepOriginalContract(string provider)
    {
        using var f = new Fixture(provider: provider);
        foreach (var form in new[] { "/admin/auth/login/", "/ADMIN/AUTH/LOGIN", "/Admin/Auth/Login/" })
        {
            using var json = await f.Client.PostAsync(form, new StringContent("{broken", Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
            Assert.Contains("The request is invalid.", await json.Content.ReadAsStringAsync(Ct));
            using var text = await f.Client.PostAsync(form, new StringContent("{broken", Encoding.UTF8, "text/plain"), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, text.StatusCode);
            Assert.Contains("Authentication is required.", await text.Content.ReadAsStringAsync(Ct));
        }
        using var method = await f.Client.GetAsync("/admin/auth/method/", Ct); Assert.Contains("password", await method.Content.ReadAsStringAsync(Ct));
        using var start = await f.Client.GetAsync("/admin/auth/start/", Ct); Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode); Assert.False(start.Headers.Contains("Set-Cookie"));
        using var callback = await f.Client.GetAsync("/admin/auth/callback/?code=sensitive-code-marker", Ct); Failure(callback, "sign_in_failed"); Assert.False(callback.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("failure", 500, true)]
    [InlineData("busy", 503, true)]
    [InlineData("cancel", 302, true)]
    [InlineData("lost-commit", 500, false)]
    public async Task ActualStorageFaults_PreservePrecommitAndNeverRetryUnknownCommit(string mode, int status, bool oldSurvives)
    {
        var fault = new WriteFault(mode);
        using var f = new Fixture(interceptors: [fault, new CommitFault(fault, mode)]);
        var old = await f.Seed(); var t = await f.Start(); fault.Enabled = true;
        using var response = await f.Callback(t, AdminSessionCookie.Name + "=" + old);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
        if (status == 503) Assert.NotNull(response.Headers.RetryAfter);
        fault.Enabled = false;
        using var existing = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + old);
        Assert.Equal(oldSurvives ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, existing.StatusCode);
        using var replay = await f.Callback(t); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.Posts);
        using var scope = f.Host.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.CountAsync(Ct));
        Assert.DoesNotContain(f.Logs.Messages, l => l.Contains("synthetic-storage-secret-marker", StringComparison.Ordinal));
        f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task DisconnectDuringExchange_ConsumesWithoutChangingExistingSession()
    {
        using var f = new Fixture(); var old = await f.Seed(); var t = await f.Start();
        f.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, f.Query(t));
        request.Headers.Add("Cookie", t.Cookie + "; " + AdminSessionCookie.Name + "=" + old);
        var sending = f.Client.SendAsync(request, cancel.Token);
        await f.Entered.Task.WaitAsync(Ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        f.Gate.TrySetResult();
        using var existing = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=" + old);
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        using var replay = await f.Callback(t); Failure(replay, "sign_in_failed"); Assert.Equal(1, f.Posts);
        f.AssertSafeLogs(t);
    }

    [Fact]
    public async Task CodeMode_SourcePriorityCsrfPublicAndHealthRemainCompatible()
    {
        using var f = new Fixture(); var old = await f.Seed();
        var bearerStudent = f.Token("at+jwt", f.Claims("student"));
        var legacyAdmin = f.Token("JWT", f.Claims());
        var cookies = AdminSessionCookie.Name + "=" + old + "; lexarborAdmin=" + legacyAdmin;
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session"); request.Headers.Add("Cookie", cookies);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerStudent);
        using var denied = await f.Client.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var invalidNew = await f.Send("/admin/auth/session", AdminSessionCookie.Name + "=bad; lexarborAdmin=" + legacyAdmin); Assert.Equal(HttpStatusCode.Unauthorized, invalidNew.StatusCode);
        using var csrf = await f.Send("/admin/vocabulary-books", cookies, "POST", JsonContent.Create(new { bookName = "CSRF", status = true })); Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
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

    [Theory]
    [InlineData("Oidc", "text/plain", "{broken", 401, "Authentication is required.")]
    [InlineData("Gateway", "text/plain", "{broken", 401, "Authentication is required.")]
    [InlineData("Oidc", "application/json", "{broken", 400, "The request is invalid.")]
    [InlineData("Gateway", "application/json", "{}", 400, "Username and password are required.")]
    public async Task OldPasswordBinding_RetainsOriginalJsonAndContentTypeContract(string provider, string type, string body, int status, string message)
    {
        using var f = new Fixture(provider: provider);
        using var response = await f.Client.PostAsync("/admin/auth/login", new StringContent(body, Encoding.UTF8, type), Ct);
        Assert.Equal(status, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (message.Length == 0) Assert.Empty(text); else Assert.Contains(message, text);
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
    public async Task CancelBeforeConsumption_DoesNotDestroyValidPendingTransaction()
    {
        using var f = new Fixture(); var t = await f.Start();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await f.Host.Server.SendAsync(context =>
        {
            var uri = new Uri("https://lexarbor.test" + f.Query(t));
            context.Request.Method = "GET"; context.Request.Path = uri.AbsolutePath;
            context.Request.QueryString = new QueryString(uri.Query); context.Request.Headers.Cookie = t.Cookie;
            context.RequestAborted = cancel.Token;
        }, Ct);
        Assert.Equal(0, f.Posts);
        using var valid = await f.Callback(t); Assert.Equal("/#/books", valid.Headers.Location!.OriginalString);
        Assert.Equal(1, f.Posts); f.AssertSafeLogs(t);
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
        public const string Issuer = "https://issuer.test";
        public const string Redirect = "https://lexarbor.test/admin/auth/callback?registered=1";
        public const string Secret = "synthetic-hosted-secret-marker";
        public static readonly string Code = WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes("synthetic-code-marker-0123456789"));
        private readonly RSA _rsa = RSA.Create(2048);
        public VocabularyWebApplicationFactory Base { get; }
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client { get; }
        public Metadata Manager { get; }
        public Clock Clock { get; } = new();
        public Logs Logs { get; } = new();
        public string? Defect { get; set; }
        public string LastAccess { get; private set; } = "";
        public string LastId { get; private set; } = "";
        public ConcurrentDictionary<string, string> Nonces { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _posts;
        public int Posts => _posts;
        public Dictionary<string, string> Form { get; private set; } = new();
        public Fixture(Dictionary<string, string?>? extra = null, string provider = "OidcCode", IInterceptor[]? interceptors = null)
        {
            var config = new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = Issuer,
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = Secret,
                ["AdminAuthentication:OidcCode:RedirectUri"] = Redirect,
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
                    if (interceptors is not null) services.AddDbContext<VocabularyDbContext>(o => o.AddInterceptors(interceptors));
                });
            });
            Client = Host.CreateClient(new() { BaseAddress = new Uri("https://lexarbor.test"), AllowAutoRedirect = false, HandleCookies = false });
        }
        public async Task<Transaction> Start(string? target = null)
        {
            using var response = await Client.GetAsync("/admin/auth/start" + (target is null ? "" : "?returnUrl=" + Uri.EscapeDataString(target)), Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            var state = parameters["state"].ToString(); var nonce = parameters["nonce"].ToString();
            Nonces[state] = nonce; Challenges[parameters["code_challenge"].ToString()] = nonce;
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            return new(state, nonce, cookie.Split(';')[0], cookie, parameters);
        }
        public string Query(Transaction t, string? error = null, string path = "/admin/auth/callback") => path + "?registered=1&state=" + t.State + "&iss=" + Uri.EscapeDataString(Issuer)
            + (error is null ? "&code=" + Code : "&error=" + error);
        public Task<HttpResponseMessage> Callback(Transaction t, string extraCookie = "") => Send(Query(t), t.Cookie + (extraCookie.Length == 0 ? "" : "; " + extraCookie));
        public Task<HttpResponseMessage> Send(string path, string cookie = "", string method = "GET", HttpContent? body = null, bool csrf = false)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
            if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
            if (csrf) request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return Client.SendAsync(request, Ct);
        }
        public async Task<string> Seed()
        {
            using var scope = Host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
            { AccessToken = "synthetic-old-access-marker", IdToken = "synthetic-old-id-marker", Issuer = Issuer, Subject = "old-sub", DisplayName = "old-user", Roles = ["admin"], AccessTokenExpiresAt = Clock.Now.AddMinutes(15) }, Ct);
        }
        public Dictionary<string, object> Claims(string? role = null) => new()
        { ["iss"] = Issuer, ["aud"] = "client-id", ["sub"] = "account-42", ["iat"] = Clock.Now.ToUnixTimeSeconds() - 60, ["exp"] = Clock.Now.ToUnixTimeSeconds() + 900, ["name"] = "access-user", ["role"] = role ?? "admin" };
        public string Token(string type, Dictionary<string, object> claims, string? defect = null)
        {
            var header = new Dictionary<string, object> { ["alg"] = "RS256", ["kid"] = "key-1", ["typ"] = type };
            if (defect == "typ") header["typ"] = "wrong";
            if (defect == "kid") header["kid"] = "absent";
            if (defect == "alg") header["alg"] = "HS256";
            if (defect == "extra-aud") claims["aud"] = new[] { "client-id", "other-client" };
            if (defect == "empty-sub") claims["sub"] = "";
            if (defect?.StartsWith("missing-", StringComparison.Ordinal) == true) claims.Remove(defect[8..]);
            if (defect is "aud" or "iss") claims[defect] = "wrong";
            if (defect == "exp") claims["exp"] = Clock.Now.ToUnixTimeSeconds();
            if (defect == "iat") claims["iat"] = Clock.Now.ToUnixTimeSeconds() + 1;
            if (defect == "missing-sub") claims.Remove("sub");
            var json = JsonSerializer.Serialize(claims);
            if (defect?.StartsWith("duplicate-", StringComparison.Ordinal) == true)
            {
                var field = defect[10..];
                json = json[..^1] + ",\"" + field + "\":" + JsonSerializer.Serialize(claims[field]) + "}";
            }
            var signing = Base64UrlEncoder.Encode(JsonSerializer.Serialize(header)) + "." + Base64UrlEncoder.Encode(json);
            var signature = _rsa.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (defect == "signature") signature[0] ^= 1;
            return signing + "." + Base64UrlEncoder.Encode(signature);
        }
        public void AssertSafeLogs(Transaction t)
        {
            foreach (var marker in new[] { Code, Secret, LastAccess, LastId, Form.GetValueOrDefault("code_verifier", ""), t.Cookie.Split('=')[1], "sensitive-description-marker", "sensitive-code-marker", "sensitive-unknown-error-marker", "synthetic-old-access-marker", "synthetic-old-id-marker", "?registered=1&state=" })
                if (marker.Length > 0) Assert.DoesNotContain(Logs.Messages, line => line.Contains(marker, StringComparison.Ordinal));
        }
        public void Dispose() { Client.Dispose(); Host.Dispose(); Base.Dispose(); _rsa.Dispose(); }
        private sealed class Handler(Fixture f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref f._posts);
                var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
                f.Form = form;
                Assert.Equal(Issuer + "/token", request.RequestUri!.AbsoluteUri); Assert.Null(request.Headers.Authorization);
                f.Entered.TrySetResult(); if (f.Gate is not null) await f.Gate.Task.WaitAsync(cancellationToken);
                if (f.Defect == "network") throw new HttpRequestException(Code);
                if (f.Defect == "timeout") throw new TaskCanceledException(Code);
                if (f.Defect is "307" or "308") return new((HttpStatusCode)int.Parse(f.Defect)) { Content = new StringContent("{}"), Headers = { Location = new Uri("https://evil.test/" + Code) } };
                if (f.Defect == "oversize-body") return new(HttpStatusCode.OK) { Content = new StringContent(new string('a', 65537)) };
                if (f.Defect == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{" + Code) };
                if (f.Defect == "invalid-grant") return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant", error_description = Code }) };
                // Select nonce from the PKCE challenge: transaction-specific even under concurrent calls.
                var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
                // Start registers nonce keyed by challenge below via the published authorization URI.
                var nonce = f.Challenges[challenge];
                var access = f.Claims(f.Defect is "nonadmin" or "id-role" ? "student" : "admin");
                var id = f.Claims(); id.Remove("role"); id["exp"] = f.Clock.Now.ToUnixTimeSeconds() + 300; id["name"] = "id-user"; id["nonce"] = f.Defect == "nonce" ? "wrong" : nonce;
                if (f.Defect == "id-role") id["role"] = "admin";
                if (f.Defect == "sub") id["sub"] = "other";
                var parts = f.Defect?.Split(':');
                f.LastAccess = f.Token("at+jwt", access, parts is { Length: 2 } && parts[0] == "access" ? parts[1] : null);
                f.LastId = f.Token("JWT", id, parts is { Length: 2 } && parts[0] == "id" ? parts[1] : null);
                var body = new Dictionary<string, object> { ["access_token"] = f.LastAccess, ["id_token"] = f.LastId, ["token_type"] = "Bearer", ["expires_in"] = 900, ["scope"] = "openid profile" };
                if (f.Defect == "token-type") body["token_type"] = "Basic";
                if (f.Defect == "expires") body["expires_in"] = 0;
                if (f.Defect == "scope") body["scope"] = "openid offline_access";
                if (f.Defect == "oversize-token") body["access_token"] = new string('a', 8193);
                if (f.Defect == "bad-token") body["id_token"] = "a.b.c";
                if (f.Defect == "missing-id") body.Remove("id_token"); if (f.Defect == "missing-access") body.Remove("access_token");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            }
        }
        public ConcurrentDictionary<string, string> Challenges { get; } = new();
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Metadata(SecurityKey key) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public SecurityKey Key { get; } = key;
        public bool Fail { get; set; }
        public OpenIdConnectConfiguration Configuration { get; } = new() { Issuer = Fixture.Issuer, AuthorizationEndpoint = Fixture.Issuer + "/authorize", TokenEndpoint = Fixture.Issuer + "/token", JwksUri = Fixture.Issuer + "/jwks" };
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        { cancel.ThrowIfCancellationRequested(); if (Fail) throw new HttpRequestException(Fixture.Code); if (Configuration.SigningKeys.Count == 0) Configuration.SigningKeys.Add(Key); return Task.FromResult(Configuration); }
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
