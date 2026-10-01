using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ServiceMantle.Web.Management;

namespace Lexarbor.Service.Tests;

public sealed class AdminSessionAuthenticationTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private static HttpClient Client(WebApplicationFactory<Program> host) => host.CreateClient(new()
    { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private static ValidatedAdminSession Session(string role = "admin", DateTimeOffset? expiry = null) => new()
    {
        AccessToken = "synthetic-access-marker",
        IdToken = "synthetic-id-marker",
        Issuer = "trusted-issuer",
        Subject = "session-subject",
        DisplayName = "session-user",
        Roles = [role, role.ToUpperInvariant()],
        AccessTokenExpiresAt = expiry ?? DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private async Task<string> Seed(WebApplicationFactory<Program> host, string role = "admin", DateTimeOffset? expiry = null)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(Session(role, expiry), Ct);
    }

    private static string Cookies(string? handle, string? legacy = null) =>
        (handle is null ? "" : $"{AdminSessionCookie.Name}={handle}")
        + (legacy is null ? "" : $"; {VocabularyWebApplicationFactory.CookieName}={legacy}");

    private Task<HttpResponseMessage> Send(HttpClient client, string method, string path, string cookies = "", string? bearer = null, bool csrf = false)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (cookies.Length > 0) request.Headers.Add("Cookie", cookies.TrimStart(';', ' '));
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        if (csrf) request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (method == "POST" && path == "/admin/vocabulary-books") request.Content = JsonContent.Create(new { bookName = "Session book", status = true });
        return client.SendAsync(request, Ct);
    }

    [Theory]
    [InlineData("new-admin", 200, 200)]
    [InlineData("old-admin", 401, 401)]
    [InlineData("bearer-admin", 200, 200)]
    [InlineData("dual-admin", 200, 200)]
    [InlineData("all-admin", 200, 200)]
    [InlineData("none", 401, 401)]
    [InlineData("new-student", 403, 403)]
    [InlineData("old-student", 401, 401)]
    [InlineData("bearer-student", 403, 403)]
    [InlineData("dual-student", 403, 403)]
    [InlineData("bad-bearer", 401, 401)]
    [InlineData("bad-new", 401, 401)]
    [InlineData("empty-new", 401, 401)]
    public async Task SourceMatrix_SelectsOneIdentityWithoutFallback(string source, int getStatus, int writeStatus)
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var handle = await Seed(host, source is "new-student" or "dual-student" ? "student" : "admin");
        // The legacy password-login cookie ("old-*", "dual-*") is retained in the
        // matrix to prove it authenticates nothing on its own and changes nothing
        // when presented alongside a live session.
        string? legacy = source.StartsWith("old", StringComparison.Ordinal) || source.StartsWith("dual", StringComparison.Ordinal)
            || source is "all-admin" or "bad-bearer" or "empty-bearer" or "bad-new" or "empty-new"
            ? host.CreateToken(source == "old-student" ? "student" : "admin") : null;
        string? selectedHandle = source.StartsWith("new", StringComparison.Ordinal) || source.StartsWith("dual", StringComparison.Ordinal)
            || source is "all-admin" or "bad-bearer" or "empty-bearer" ? handle : source == "bad-new" ? "bad" : source == "empty-new" ? "" : null;
        string? bearer = source is "bearer-admin" or "all-admin" ? host.CreateToken("admin")
            : source == "bearer-student" ? host.CreateToken("student") : source == "bad-bearer" ? "bad" : source == "empty-bearer" ? "" : null;
        var cookie = Cookies(selectedHandle, legacy);
        var get = await Send(client, "GET", "/admin/auth/session", cookie, bearer);
        var write = await Send(client, "POST", "/admin/vocabulary-books", cookie, bearer, csrf: true);
        Assert.Equal(getStatus, (int)get.StatusCode);
        Assert.Equal(writeStatus, (int)write.StatusCode);
        Assert.Null(get.Headers.Location);
        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Ct));
        Assert.Equal(getStatus == 200, body.RootElement.GetProperty("success").GetBoolean());
        if (getStatus == 200)
        {
            var newIdentity = source is "new-admin" or "dual-admin";
            Assert.Equal(newIdentity ? "session-user" : "test-user", body.RootElement.GetProperty("data").GetProperty("username").GetString());
            Assert.Single(body.RootElement.GetProperty("data").GetProperty("roles").EnumerateArray());
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/vocabulary-books/all", Ct)).StatusCode);
    }

    [Fact]
    public async Task EmptyBearerPrefix_DoesNotFallbackToEitherCookie()
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var handle = await Seed(host);
        foreach (var method in new[] { "GET", "POST" })
        {
            // HttpClient strips trailing whitespace from Authorization. Exercise
            // the exact server-side prefix contract without that normalization.
            var result = await host.Server.SendAsync(context =>
            {
                context.Request.Method = method;
                context.Request.Path = method == "GET" ? "/admin/auth/session" : "/admin/vocabulary-books";
                context.Request.Headers.Authorization = "Bearer ";
                context.Request.Headers.Cookie = Cookies(handle, host.CreateToken("admin"));
                context.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
            }, Ct);
            Assert.Equal(401, result.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(true, "student")]
    public async Task CookieCsrf_RejectsWriteAndLogoutWithoutSideEffects(bool dual, string role)
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var handle = await Seed(host, role);
        var cookies = Cookies(handle, dual ? host.CreateToken("admin") : null);
        foreach (var path in new[] { "/admin/vocabulary-books", "/admin/auth/logout" })
        {
            var response = await Send(client, "POST", path, cookies);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        using var scope = host.Services.CreateScope();
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().ReadAsync(handle, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_RevokesCopiedHandleAndDeletesBothCookies_WhileBearerSurvives(bool bearer)
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var handle = await Seed(host);
        var token = bearer ? host.CreateToken("admin") : null;
        var cookies = Cookies(handle, host.CreateToken("admin"));
        if (bearer) Assert.Equal(HttpStatusCode.OK, (await Send(client, "POST", "/admin/vocabulary-books", cookies, token)).StatusCode);
        var logout = await Send(client, "POST", "/admin/auth/logout", cookies, token, csrf: !bearer);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        AssertBothDeleted(logout.Headers.GetValues("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "POST", "/admin/auth/logout", cookies)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "POST", "/admin/auth/logout")).StatusCode);
        if (bearer) Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/admin/auth/session", bearer: token)).StatusCode);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("expired")]
    [InlineData("corrupt")]
    public async Task UnusableSession_FailsClosedIncludingWrites_ButLogoutCleansIt(string kind)
    {
        var clock = new Clock();
        using var host = new VocabularyWebApplicationFactory();
        using var configured = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        using var client = Client(configured);
        var handle = kind == "empty" ? "" : kind == "invalid" ? "invalid" : await Seed(configured, expiry: clock.Now.AddSeconds(5));
        if (kind == "expired") clock.Now = clock.Now.AddSeconds(5);
        if (kind == "corrupt")
        {
            using var scope = configured.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Database.ExecuteSqlRawAsync("UPDATE admin_session SET protected_payload = 'broken'", Ct);
        }
        var cookies = Cookies(handle, host.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "GET", "/admin/auth/session", cookies)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "POST", "/admin/vocabulary-books", cookies)).StatusCode);
        var logout = await Send(client, "POST", "/admin/auth/logout", cookies);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        AssertBothDeleted(logout.Headers.GetValues("Set-Cookie"));
    }

    [Theory]
    [InlineData(false, 1, 500)]
    [InlineData(false, 5, 503)]
    [InlineData(true, 1, 500)]
    [InlineData(true, 5, 503)]
    public async Task Logout_ReadAndRevokeFailures_ClearCookiesAndDoNotClaimRevocation(bool revoke, int errorCode, int status)
    {
        var failure = new StorageFailure(revoke, errorCode);
        var logs = new Logs();
        using var host = new VocabularyWebApplicationFactory();
        using var configured = host.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddDbContext<VocabularyDbContext>(options => options.AddInterceptors(failure)));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        using var client = Client(configured);
        var handle = await Seed(configured);
        failure.Enabled = true;
        logs.Messages.Clear();
        foreach (var path in new[] { "/admin/auth/logout", "/admin/auth/logout/", "/ADMIN/AUTH/LOGOUT" })
        {
            var response = await Send(client, "POST", path, Cookies(handle, host.CreateToken("admin")), csrf: true);
            Assert.Equal(status, (int)response.StatusCode);
            AssertBothDeleted(response.Headers.GetValues("Set-Cookie"));
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.Contains(AdminSessionRepository.StorageFailureMessage, body);
            Assert.True(!body.Contains("synthetic-storage-secret", StringComparison.Ordinal));
            if (status == 503) Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
        }
        Assert.True(logs.Messages.All(message => !message.Contains("synthetic-storage-secret", StringComparison.Ordinal)
            && !message.Contains(handle, StringComparison.Ordinal) && !message.Contains("admin_session", StringComparison.Ordinal)));
        failure.Enabled = false;
        // No commit occurred in these controlled failures: the copied handle still works.
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
    }

    [Fact]
    public async Task InternalSignIn_UsesVerifiedExpiryOpaqueCookieAndAtomicallyReplaces()
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var old = await Seed(host);
        using var scope = host.Services.CreateScope();
        var token = host.CreateToken("ADMIN");
        var principal = (await scope.ServiceProvider.GetRequiredService<AdminAccessTokenValidator>().ValidateAsync(token, Ct))!;
        var context = Context(scope.ServiceProvider, old);
        await scope.ServiceProvider.GetRequiredService<IAdminSessionSignIn>().SignInAsync(context, principal, token, "synthetic-id-marker", Ct);
        var headers = context.Response.Headers.SetCookie.Select(value => value!).ToArray();
        var cookie = headers.Single(value => value.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
        var handle = Handle(cookie);
        Assert.Equal(43, handle.Length);
        Assert.True(headers.All(value => !value.Contains(token, StringComparison.Ordinal) && !value.Contains("synthetic-id-marker", StringComparison.Ordinal)));
        foreach (var attribute in new[] { "secure", "httponly", "samesite=lax", "path=/" }) Assert.Contains(attribute, cookie.ToLowerInvariant());
        Assert.DoesNotContain("domain=", cookie.ToLowerInvariant());
        var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirst("exp")!.Value, CultureInfo.InvariantCulture));
        var remaining = long.Parse(cookie.Split(';').Single(part => part.TrimStart().StartsWith("max-age=", StringComparison.Ordinal)).Split('=')[1], CultureInfo.InvariantCulture);
        Assert.True(remaining <= (expiry - DateTimeOffset.UtcNow).TotalSeconds + 1);
        var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
        Assert.Null(await store.ReadAsync(old, Ct));
        Assert.Equal(expiry, (await store.ReadAsync(handle, Ct))!.AccessTokenExpiresAt);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/admin/system/version", Cookies(handle))).StatusCode);
        // Delivery is deliberately delayed until after revocation. The late cookie cannot recreate its row.
        await store.RevokeAndReadAsync(handle, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "GET", "/admin/auth/session", Cookies(Handle(cookie)))).StatusCode);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("student")]
    [InlineData("issuer")]
    [InlineData("subject")]
    [InlineData("expiry")]
    [InlineData("expired")]
    [InlineData("token")]
    public async Task InternalSignIn_RejectsUntrustedOrInvalidInputWithoutCookieOrRows(string kind)
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        using var scope = host.Services.CreateScope();
        List<Claim> claims = [new("iss", "issuer"), new("sub", "subject"), new("role", kind == "student" ? "student" : "admin"),
            new("exp", DateTimeOffset.UtcNow.AddMinutes(kind == "expired" ? -1 : 5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))];
        if (kind is "issuer" or "subject" or "expiry") claims.RemoveAll(claim => claim.Type == (kind == "issuer" ? "iss" : kind == "subject" ? "sub" : "exp"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, kind == "anonymous" ? null : "validated"));
        var context = Context(scope.ServiceProvider);
        await Assert.ThrowsAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IAdminSessionSignIn>()
            .SignInAsync(context, principal, kind == "token" ? "" : "synthetic-access-marker", cancellationToken: Ct));
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("lost-commit")]
    public async Task InternalSignIn_FailedOrCancelledCommitNeverWritesCookie_AndLostCommitIsUnknown(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var fault = new SignInFault(mode, cancellation);
        using var host = new VocabularyWebApplicationFactory();
        using var configured = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<VocabularyDbContext>(options => options.AddInterceptors(fault, new CommitFault(fault, mode)))));
        using var client = Client(configured);
        var old = await Seed(configured);
        using var scope = configured.Services.CreateScope();
        var principal = (await scope.ServiceProvider.GetRequiredService<AdminAccessTokenValidator>().ValidateAsync(host.CreateToken("admin"), Ct))!;
        var context = Context(scope.ServiceProvider, old);
        fault.Enabled = true;
        var signIn = scope.ServiceProvider.GetRequiredService<IAdminSessionSignIn>().SignInAsync(context, principal,
            "synthetic-access-marker", cancellationToken: cancellation.Token);
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
        else await Assert.ThrowsAsync<AdminSessionStorageException>(() => signIn);
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        fault.Enabled = false;
        using var verify = configured.Services.CreateScope();
        var recovered = await verify.ServiceProvider.GetRequiredService<AdminSessionStore>().ReadAsync(old, Ct);
        Assert.Equal(mode != "lost-commit", recovered is not null);
        Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task ReadBeforeLogout_CanFinish_ButSubsequentReadFails()
    {
        var gate = new ReadGate();
        using var host = new VocabularyWebApplicationFactory();
        using var configured = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IAuthorizationHandler>(gate)));
        using var client = Client(configured);
        var handle = await Seed(configured);
        var started = Send(client, "GET", "/admin/auth/session", Cookies(handle));
        await gate.Entered.Task.WaitAsync(Ct);
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(client, "POST", "/admin/auth/logout", Cookies(handle), csrf: true)).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await started).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
    }

    [Fact]
    public async Task InternalSignIn_LostCookieResponseDoesNotCompensateCommittedReplacement()
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var old = await Seed(host);
        using var scope = host.Services.CreateScope();
        var principal = (await scope.ServiceProvider.GetRequiredService<AdminAccessTokenValidator>()
            .ValidateAsync(host.CreateToken("admin"), Ct))!;
        var context = Context(scope.ServiceProvider, old);
        context.Features.Set<IHttpResponseFeature>(new HttpResponseFeature
        { Headers = new HeaderDictionary { IsReadOnly = true } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAdminSessionSignIn>()
            .SignInAsync(context, principal, "synthetic-access-marker", cancellationToken: Ct));
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().ReadAsync(old, Ct));
        // The replacement is committed despite lost delivery; it is never replayed or rolled back.
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task InternalSignIn_WaitingCancellationPreservesOld_AndConcurrentSignInsAreIndependent()
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var old = await Seed(host);
        using var blocker = host.Services.CreateScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = blocker.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async () =>
        { entered.SetResult(); await release.Task; return 0; }, Ct);
        await entered.Task.WaitAsync(Ct);
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        using var cancelled = host.Services.CreateScope();
        var principal = (await first.ServiceProvider.GetRequiredService<AdminAccessTokenValidator>().ValidateAsync(host.CreateToken("admin"), Ct))!;
        using var cancellation = new CancellationTokenSource();
        var cancelContext = Context(cancelled.ServiceProvider, old);
        var waiting = cancelled.ServiceProvider.GetRequiredService<IAdminSessionSignIn>().SignInAsync(cancelContext, principal,
            "synthetic-access-marker", cancellationToken: cancellation.Token);
        var a = Context(first.ServiceProvider, old);
        var b = Context(second.ServiceProvider, old);
        var signA = first.ServiceProvider.GetRequiredService<IAdminSessionSignIn>().SignInAsync(a, principal, "synthetic-access-marker", cancellationToken: Ct);
        var signB = second.ServiceProvider.GetRequiredService<IAdminSessionSignIn>().SignInAsync(b, principal, "synthetic-access-marker", cancellationToken: Ct);
        try
        {
            Assert.False(waiting.IsCompleted || signA.IsCompleted || signB.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(0, cancelContext.Response.Headers.SetCookie.Count);
            Assert.NotNull(await first.ServiceProvider.GetRequiredService<AdminSessionStore>().ReadAsync(old, Ct));
        }
        finally { release.TrySetResult(); }
        await transaction;
        await Task.WhenAll(signA, signB);
        Assert.Equal(2, await first.ServiceProvider.GetRequiredService<VocabularyDbContext>().AdminSessions.CountAsync(Ct));
        foreach (var context in new[] { a, b })
        {
            var handle = Handle(context.Response.Headers.SetCookie.First(value => value!.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal))!);
            Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "GET", "/admin/auth/session", Cookies(old))).StatusCode);
    }

    [Fact]
    public async Task Restart_RestoresHttpIdentity_AndMissingRingFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lexarbor-http-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "sessions.db");
        try
        {
            string handle;
            using (var first = new VocabularyWebApplicationFactory("Testing", true, keyContentRoot: root, databasePath: database))
            {
                using var client = Client(first);
                using var scope = first.Services.CreateScope();
                var token = first.CreateToken("admin");
                var principal = (await scope.ServiceProvider.GetRequiredService<AdminAccessTokenValidator>().ValidateAsync(token, Ct))!;
                var context = Context(scope.ServiceProvider);
                await scope.ServiceProvider.GetRequiredService<IAdminSessionSignIn>().SignInAsync(context, principal, token, cancellationToken: Ct);
                handle = Handle(context.Response.Headers.SetCookie.First(value => value!.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal))!);
            }
            using (var second = new VocabularyWebApplicationFactory("Testing", true, keyContentRoot: root, databasePath: database))
            using (var client = Client(second))
                Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
            // A different root key against the existing key rows is refused at
            // startup by the protect/unprotect probe: the host never serves.
            Assert.ThrowsAny<Exception>(() =>
            {
                using var lost = new VocabularyWebApplicationFactory("Testing", true,
                    keyContentRoot: Path.Combine(root, "new-ring"), databasePath: database);
                using var lostClient = Client(lost);
                return lostClient;
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConfiguredRoleAndSubjectFallback_AreAppliedToNewSession()
    {
        using var host = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = "operator" });
        using var client = Client(host);
        using var scope = host.Services.CreateScope();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "issuer"), new Claim(ClaimTypes.NameIdentifier, "subject-fallback"),
            new Claim("name", " "), new Claim(ClaimTypes.Role, "OPERATOR"), new Claim("role", "operator"),
            new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], "validated"));
        var context = Context(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IAdminSessionSignIn>().SignInAsync(context, principal, "synthetic-access-marker", cancellationToken: Ct);
        var handle = Handle(context.Response.Headers.SetCookie.First(value => value!.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal))!);
        var response = await Send(client, "GET", "/admin/auth/session", Cookies(handle));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("subject-fallback", json.RootElement.GetProperty("data").GetProperty("username").GetString());
        Assert.Single(json.RootElement.GetProperty("data").GetProperty("roles").EnumerateArray());
    }

    private static DefaultHttpContext Context(IServiceProvider services, string? handle = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        if (handle is not null) context.Request.Headers.Cookie = Cookies(handle);
        return context;
    }
    private static string Handle(string cookie) => cookie.Split(';')[0][(AdminSessionCookie.Name.Length + 1)..];
    private static void AssertBothDeleted(IEnumerable<string> cookies)
    {
        var values = cookies.ToArray();
        Assert.Equal(2, values.Length);
        foreach (var name in new[] { AdminSessionCookie.Name, VocabularyWebApplicationFactory.CookieName })
            Assert.Contains(values, value => value.StartsWith(name + "=;", StringComparison.Ordinal) && value.Contains("expires=", StringComparison.OrdinalIgnoreCase) && value.Contains("path=/", StringComparison.OrdinalIgnoreCase));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class StorageFailure(bool revoke, int code) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !revoke && command.CommandText.Contains("admin_session", StringComparison.Ordinal)) throw new SqliteException("synthetic-storage-secret", code);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && revoke && command.CommandText.StartsWith("DELETE", StringComparison.Ordinal) && command.CommandText.Contains("admin_session", StringComparison.Ordinal)) throw new SqliteException("synthetic-storage-secret", code);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class SignInFault(string mode, CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("INSERT INTO admin_session", StringComparison.Ordinal))
            {
                if (mode == "cancel") cancellation.Cancel();
                if (mode == "failure") throw new SqliteException("synthetic-storage-secret", 1);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CommitFault(SignInFault fault, string mode) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (fault.Enabled && mode == "lost-commit") throw new SqliteException("synthetic-storage-secret", 1);
            return Task.CompletedTask;
        }
    }
    // Holds one administrator request inside authorization (the ServiceMantle
    // management-permission requirement the /admin routes now use), so a test
    // can act while that request is between authentication and the endpoint.
    private sealed class ReadGate : AuthorizationHandler<ManagementPermissionRequirement>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagementPermissionRequirement requirement)
        {
            if (context.Resource is HttpContext http && http.Request.Path == "/admin/auth/session" && context.User.Identity?.IsAuthenticated == true && !Release.Task.IsCompleted)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(http.RequestAborted); }
        }
    }
    private sealed class Logs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void Dispose() { }
        private sealed class Recorder(Logs logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { logs.Messages.Add(formatter(state, exception) + exception?.ToString()); }
        }
    }
}
