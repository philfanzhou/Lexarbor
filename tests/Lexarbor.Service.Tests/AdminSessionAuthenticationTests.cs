using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authorization;
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
        var cookieParts = new List<string>();
        if (cookies.Length > 0) cookieParts.Add(cookies.TrimStart(';', ' '));
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        if (csrf)
        {
            var (token, antiforgery) = AntiforgeryFor(client);
            request.Headers.Add(AdminTestAntiforgery.HeaderName, token);
            cookieParts.Add(antiforgery);
        }
        if (cookieParts.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookieParts));
        if (method == "POST" && path == "/admin/vocabulary-books") request.Content = JsonContent.Create(new { bookName = "Session book", status = true });
        return client.SendAsync(request, Ct);
    }

    private sealed class AntiforgeryPair(string token, string cookie)
    {
        public string Token { get; } = token;
        public string Cookie { get; } = cookie;
    }

    private static readonly ConditionalWeakTable<HttpClient, AntiforgeryPair> AntiforgeryPairs = new();

    private static (string Token, string Cookie) AntiforgeryFor(HttpClient client)
    {
        if (AntiforgeryPairs.TryGetValue(client, out var cached)) return (cached.Token, cached.Cookie);
        using var response = client.GetAsync("/admin/auth/csrf", TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(response.Content.ReadAsStream(TestContext.Current.CancellationToken));
        var pair = new AntiforgeryPair(body.RootElement.GetProperty("token").GetString()!,
            response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        AntiforgeryPairs.Add(client, pair);
        return (pair.Token, pair.Cookie);
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
            // The session endpoint reflects the hosted session when one is presented
            // (the cookie is its subject) and the verified Bearer identity otherwise.
            var sessionIdentity = source.StartsWith("new", StringComparison.Ordinal)
                || source.StartsWith("dual", StringComparison.Ordinal)
                || source is "all-admin";
            Assert.Equal(sessionIdentity ? "session-user" : "test-user", body.RootElement.GetProperty("data").GetProperty("username").GetString());
            Assert.Single(body.RootElement.GetProperty("data").GetProperty("roles").EnumerateArray());
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/vocabulary-books/all", Ct)).StatusCode);
    }

    [Fact]
    public async Task EmptyBearerPrefix_DoesNotFallbackToEitherCookie()
    {
        using var host = new VocabularyWebApplicationFactory();
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
                var (token, antiforgery) = AdminTestAntiforgery.Get(host);
                context.Request.Headers[AdminTestAntiforgery.HeaderName] = token;
                context.Request.Headers.Cookie = Cookies(handle, host.CreateToken("admin")) + "; " + antiforgery;
            }, Ct);
            Assert.Equal(401, result.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(true, "student")]
    public async Task MissingAntiforgeryToken_RejectsWriteAndLogoutWithoutSideEffects(bool dual, string role)
    {
        using var host = new VocabularyWebApplicationFactory();
        using var client = Client(host);
        var handle = await Seed(host, role);
        var cookies = Cookies(handle, dual ? host.CreateToken("admin") : null);
        // The declared presentation change: a session write without the token fails
        // the authentication itself (the fixed 401), and the logout endpoint answers
        // the package's fixed csrf rejection. Neither touches the session.
        using var write = await Send(client, "POST", "/admin/vocabulary-books", cookies);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        Assert.Contains("Authentication is required.", await write.Content.ReadAsStringAsync(Ct));
        Assert.False(write.Headers.Contains("Set-Cookie"));
        using var logout = await Send(client, "POST", "/admin/auth/logout", cookies);
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Contains("csrf_rejected", await logout.Content.ReadAsStringAsync(Ct));
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
        if (bearer) Assert.Equal(HttpStatusCode.OK, (await Send(client, "POST", "/admin/vocabulary-books", cookies, token, csrf: true)).StatusCode);
        var logout = await Send(client, "POST", "/admin/auth/logout", cookies, token, csrf: true);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        AssertBothDeleted(logout.Headers.GetValues("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "POST", "/admin/auth/logout", cookies, csrf: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "POST", "/admin/auth/logout", csrf: true)).StatusCode);
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
        var logout = await Send(client, "POST", "/admin/auth/logout", cookies, csrf: true);
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
    public async Task Restart_RestoresHttpIdentity_AndMissingRingFailsClosed()
    {
        var root = VocabularyWebApplicationFactory.CreateGateSafeDirectory(
            $"lexarbor-http-session-{Guid.NewGuid():N}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // The database passes through the strict startup gate, so it lives in
        // the gate-safe directory; the key ring keeps its own temp root.
        var databaseDirectory = VocabularyWebApplicationFactory.CreateGateSafeDirectory(
            $"lexarbor-http-session-{Guid.NewGuid():N}");
        var database = Path.Combine(databaseDirectory, "sessions.db");
        try
        {
            string handle;
            using (var first = new VocabularyWebApplicationFactory("Testing", true, keyContentRoot: root, databasePath: database))
            {
                handle = await Seed(first);
            }
            using (var second = new VocabularyWebApplicationFactory("Testing", true, keyContentRoot: root, databasePath: database))
            using (var client = Client(second))
            {
                Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/admin/auth/session", Cookies(handle))).StatusCode);
            }
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
        finally
        {
            Directory.Delete(root, recursive: true);
            try
            {
                Directory.Delete(databaseDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A WAL sidecar SQLite still holds is left behind.
            }
        }
    }

    private static void AssertBothDeleted(IEnumerable<string> cookies)
    {
        var values = cookies.ToArray();
        // Both names are deleted; the package's own session deletion may appear
        // beside the middleware's, so the assertion is on the distinct names.
        Assert.Equal(2, values.Select(value => value.Split('=')[0]).Distinct().Count());
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
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { logs.Messages.Add(formatter(state, exception) + exception?.ToString()); }
        }
    }
}
