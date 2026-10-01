using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Pins the ServiceMantle management audit of the administrator authentication boundary:
/// hosted sign-in, the fixed-reason login failures and logout each leave exactly the row
/// the semantic model prescribes, the sign-in/logout rows commit with their session write
/// or not at all, a failed audit save never silently loses the event, and no credential
/// material (code, state, token, secret, session handle) ever reaches the audit table.
/// </summary>
public class AdminAuthenticationAuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Succeeded = "admin_login.succeeded";
    private const string Failed = "admin_login.failed";
    private const string LogoutAction = "admin_login.logout";
    private const int OutcomeFailure = 2;
    private const int OutcomeDenied = 3;

    [Fact]
    public async Task SuccessfulLogin_WritesOneSucceededRowCommittedWithTheSession()
    {
        using var f = new Fixture();
        f.ClientAddress = "203.0.113.77";
        var t = await f.Start();
        using var response = await f.Send(f.Query(t), t.Cookie);
        Assert.Equal("/#/books", response.Headers.Location!.OriginalString);
        var correlationId = response.Headers.GetValues("x-correlation-id").Single();

        Assert.Equal(1, await f.SessionCountAsync());
        var row = Assert.Single(await f.AuditAsync());
        Assert.Equal(Succeeded, row.Action);
        Assert.Equal("interactive_admin", row.OperatorSource);
        Assert.Equal("account-42", row.OperatorId);
        Assert.Null(row.OperatorDisplayName);
        Assert.Equal("admin_session", row.TargetType);
        Assert.Equal("account-42", row.TargetId);
        Assert.Equal(1, row.Outcome);
        Assert.Equal("203.0.113.77", row.ClientIp);
        Assert.Equal(correlationId, row.CorrelationId);
        Assert.Equal("Administrator signed in through the hosted login.", row.SecurityDescription);
        Assert.Null(row.MetadataJson);
        Assert.InRange(row.OccurredAtUtc, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Theory]
    [InlineData("invalid-state", "sign_in_failed", OutcomeFailure)]
    [InlineData("access_denied", "canceled", OutcomeDenied)]
    [InlineData("server_error", "provider_unavailable", OutcomeFailure)]
    [InlineData("nonadmin", "denied", OutcomeDenied)]
    [InlineData("network", "provider_unavailable", OutcomeFailure)]
    [InlineData("nonce", "sign_in_failed", OutcomeFailure)]
    public async Task FailedLogin_WritesExactlyOneAnonymousFailedRowWithTheFixedReason(
        string defect, string reason, int outcome)
    {
        using var f = new Fixture();
        f.ClientAddress = "198.51.100.9";
        var t = await f.Start();
        var query = defect switch
        {
            "invalid-state" => f.Query(t).Replace(t.State, Fixture.Code, StringComparison.Ordinal),
            "access_denied" => f.Query(t, "access_denied") + "&error_description=sensitive-description-marker",
            "server_error" => f.Query(t, "server_error") + "&error_description=sensitive-description-marker",
            _ => f.Query(t)
        };
        if (defect is "nonadmin" or "network" or "nonce") f.Defect = defect;
        using var response = await f.Send(query, t.Cookie);

        Assert.Equal("/#/login?reason=" + reason, response.Headers.Location!.OriginalString);
        Assert.Equal(0, await f.SessionCountAsync());
        var row = Assert.Single(await f.AuditAsync());
        Assert.Equal(Failed, row.Action);
        Assert.Equal("anonymous", row.OperatorSource);
        Assert.Null(row.OperatorId);
        Assert.Equal("admin_session", row.TargetType);
        Assert.Equal("unknown", row.TargetId);
        Assert.Equal(outcome, row.Outcome);
        Assert.Equal("198.51.100.9", row.ClientIp);
        Assert.NotNull(row.CorrelationId);
        Assert.Equal("{\"reason\":\"" + reason + "\"}", row.MetadataJson);
        Assert.Equal("Administrator hosted login failed.", row.SecurityDescription);
    }

    [Fact]
    public async Task Cancellation_WritesNoAuditRow()
    {
        using var f = new Fixture();
        var t = await f.Start();
        f.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, f.Query(t)) { Headers = { { "Cookie", t.Cookie } } };
        var sending = f.Client.SendAsync(request, cancellation.Token);
        await f.Entered.Task.WaitAsync(Ct);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        f.Gate.TrySetResult();
        Assert.Empty(await f.AuditAsync());
        Assert.Equal(0, await f.SessionCountAsync());
    }

    [Fact]
    public async Task LogoutWithLiveSession_WritesLogoutRowFromTheRevokedSnapshot()
    {
        using var f = new Fixture();
        f.ClientAddress = "203.0.113.78";
        var cookie = await f.SignIn();
        using var logout = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        Assert.Equal(0, await f.SessionCountAsync());
        var row = Assert.Single(await f.AuditAsync(), row => row.Action == LogoutAction);
        Assert.Equal("interactive_admin", row.OperatorSource);
        Assert.Equal("account-42", row.OperatorId);
        Assert.Equal("access-user", row.OperatorDisplayName);
        Assert.Equal("admin_session", row.TargetType);
        Assert.Equal("account-42", row.TargetId);
        Assert.Equal(1, row.Outcome);
        Assert.Equal("203.0.113.78", row.ClientIp);
        Assert.Equal("Administrator signed out; the session was revoked.", row.SecurityDescription);
        Assert.Null(row.MetadataJson);

        // A repeated logout stays idempotent and writes nothing: no live revocation,
        // no audit object, no new row.
        using var repeat = await f.Logout(cookie);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Single(await f.AuditAsync(), row => row.Action == LogoutAction);
    }

    [Fact]
    public async Task LogoutWithoutCookieOrSession_WritesNoRow()
    {
        using var f = new Fixture();
        using var anonymous = await f.Logout("");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        using var garbage = await f.Logout(AdminSessionCookie.Name + "=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal(HttpStatusCode.OK, garbage.StatusCode);
        Assert.Empty(await f.AuditAsync());
    }

    [Theory]
    [InlineData("admin_session", 1, 500)]
    [InlineData("service_audit_logs", 1, 500)]
    [InlineData("service_audit_logs", 5, 503)]
    public async Task StorageFailureInsideTheSignInTransaction_LeavesNoSucceededRowAndNoSession(
        string table, int errorCode, int status)
    {
        var fault = new InsertFault(table, errorCode);
        using var f = new Fixture([fault]);
        // Seed one live session; its own sign-in already wrote the one succeeded row.
        var old = await f.SignIn();
        var succeededBefore = await f.AuditCountAsync(Succeeded);
        var t = await f.Start();
        fault.Enabled = true;
        using var response = await f.Send(f.Query(t), t.Cookie);
        fault.Enabled = false;

        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c =>
            c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
        if (status == 503) Assert.NotNull(response.Headers.RetryAfter);
        // Whether the session write or the audit save failed, the transaction rolled
        // back whole: no new succeeded row survived next to the seeded one.
        Assert.Equal(succeededBefore, await f.AuditCountAsync(Succeeded));
        Assert.Equal(1, await f.SessionCountAsync());
        using var existing = await f.Send("/admin/auth/session", old);
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
    }

    [Fact]
    public async Task AuditSaveFailureForAFailedLogin_AnswersTheStorageFamilyInsteadOfTheRedirect()
    {
        var fault = new InsertFault("service_audit_logs", 1);
        using var f = new Fixture([fault]);
        var t = await f.Start();
        fault.Enabled = true;
        using var response = await f.Send(f.Query(t).Replace(t.State, Fixture.Code, StringComparison.Ordinal), t.Cookie);
        fault.Enabled = false;

        // The audit row is not silently dropped behind a successful failure redirect: the
        // standalone save follows the session storage failure semantics.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Administrator session storage is unavailable.",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(await f.AuditAsync());
    }

    [Fact]
    public async Task AuditTable_NeverContainsCodeStateTokenSecretOrHandleMaterial()
    {
        using var f = new Fixture();
        var t = await f.Start();
        // One success and several failures with recognizable marker material in flight.
        await f.SignIn();
        await f.Send(f.Query(t, "access_denied") + "&error_description=sensitive-description-marker", t.Cookie);
        await f.Send(f.Query(t, "server_error"), t.Cookie);
        f.Defect = "nonadmin";
        var third = await f.Start();
        await f.Send(f.Query(third), third.Cookie);

        var text = await f.AuditTableTextAsync();
        Assert.Contains(Succeeded, text);
        Assert.Contains(Failed, text);
        foreach (var marker in new[] { Fixture.Code, Fixture.Secret, f.LastAccess, f.LastId,
                     t.Cookie.Split('=')[1], third.Cookie.Split('=')[1], "sensitive-description-marker",
                     "code_verifier", "?registered=1&state=" })
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    private sealed record AuditRow(
        string Action,
        string OperatorSource,
        string? OperatorId,
        string? OperatorDisplayName,
        string TargetType,
        string TargetId,
        int Outcome,
        string? ClientIp,
        string? CorrelationId,
        string? SecurityDescription,
        string? MetadataJson,
        DateTimeOffset OccurredAtUtc);

    /// <summary>
    /// Fails the first enabled INSERT into the named table with the given SQLite error. Session
    /// SQL runs as raw non-queries while EF Core's SaveChanges INSERTs execute as readers; the
    /// reader fault is raised before execution so a standalone save has nothing committed.
    /// </summary>
    private sealed class InsertFault(string table, int errorCode) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            FailInsertInto(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailInsertInto(command);
            return ValueTask.FromResult(result);
        }

        private void FailInsertInto(DbCommand command)
        {
            // Raw session SQL is unquoted; EF Core generated SQL quotes the identifier.
            if (Enabled
                && command.CommandText.StartsWith("INSERT INTO ", StringComparison.Ordinal)
                && command.CommandText[12..].TrimStart('"').StartsWith(table, StringComparison.Ordinal))
                throw new SqliteException("synthetic-storage-secret-marker", errorCode);
        }
    }

    private sealed record Transaction(string State, string Nonce, string Cookie);

    private sealed class Fixture : IDisposable
    {
        public const string Issuer = "https://issuer.test";
        public const string Secret = "synthetic-hosted-secret-marker";
        public static readonly string Code =
            WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes("synthetic-code-marker-0123456789"));

        private readonly RSA _rsa = RSA.Create(2048);
        private readonly string _databasePath = Path.Combine(
            VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-audit-{Guid.NewGuid():N}"),
            "audit.db");

        public VocabularyWebApplicationFactory Base { get; private set; } = null!;
        public WebApplicationFactory<Program> Host { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public string? ClientAddress { get; set; }
        public string? Defect { get; set; }
        public string LastAccess { get; private set; } = "";
        public string LastId { get; private set; } = "";
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, string> Challenges { get; } = new();

        public Fixture(IInterceptor[]? interceptors = null)
        {
            var config = new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = Issuer,
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = Secret,
                ["AdminAuthentication:OidcCode:RedirectUri"] =
                    "https://lexarbor.test/admin/auth/callback?registered=1",
                ["AdminAuthentication:OidcCode:Scope"] = "openid profile",
                ["RateLimits:AdminLogin:Enabled"] = "false"
            };
            var manager = new Metadata(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "key-1" });
            Base = new VocabularyWebApplicationFactory("Testing", true, "OidcCode", config,
                databasePath: _databasePath);
            Host = Base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.ConfigurationManager = manager;
                        options.TokenValidationParameters.IssuerSigningKey = manager.Key;
                    });
                    services.AddHttpClient(AdminCodeExchange.BackchannelName)
                        .ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
                    // The prepared logout stays local-only: a bounded stub stands in for the
                    // provider's logout preparation endpoint.
                    services.AddHttpClient(AdminPreparedLogout.BackchannelName)
                        .ConfigurePrimaryHttpMessageHandler(() => new StubLogoutHandler());
                    if (interceptors is not null)
                        services.AddDbContext<Lexarbor.Database.VocabularyDbContext>(
                            options => options.AddInterceptors(interceptors));
                });
            });
            Client = Host.CreateClient(new()
            {
                BaseAddress = new Uri("https://lexarbor.test"),
                AllowAutoRedirect = false,
                HandleCookies = false
            });
        }

        public async Task<Transaction> Start()
        {
            using var response = await Client.GetAsync("/admin/auth/start", Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            var state = parameters["state"].ToString();
            var nonce = parameters["nonce"].ToString();
            Challenges[parameters["code_challenge"].ToString()] = nonce;
            var cookie = response.Headers.GetValues("Set-Cookie").Single();
            return new Transaction(state, nonce, cookie.Split(';')[0]);
        }

        public string Query(Transaction t, string? error = null) =>
            "/admin/auth/callback?registered=1&state=" + t.State + "&iss=" + Uri.EscapeDataString(Issuer)
            + (error is null ? "&code=" + Code : "&error=" + error);

        public Task<HttpResponseMessage> Send(string path, string cookie = "")
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
            ApplyClientAddress(request);
            return Client.SendAsync(request, Ct);
        }

        public async Task<string> SignIn()
        {
            var t = await Start();
            using var response = await Send(Query(t), t.Cookie);
            Assert.Equal("/#/books", response.Headers.Location!.OriginalString);
            return response.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal))
                .Split(';')[0];
        }

        public async Task<HttpResponseMessage> Logout(string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
            if (cookie.Length > 0)
            {
                request.Headers.Add("Cookie", cookie);
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            }

            ApplyClientAddress(request);
            return await Client.SendAsync(request, Ct);
        }

        private void ApplyClientAddress(HttpRequestMessage request)
        {
            if (ClientAddress is not null)
                request.Headers.Add(VocabularyWebApplicationFactory.ClientAddressHeader, ClientAddress);
        }

        public async Task<List<AuditRow>> AuditAsync()
        {
            var rows = new List<AuditRow>();
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT action, operator_source, operator_id, operator_display_name, target_type, target_id,
                       outcome, client_ip, correlation_id, security_description, metadata_json, occurred_at_utc
                FROM service_audit_logs ORDER BY rowid
                """;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(new AuditRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
            }

            return rows;
        }

        public async Task<string> AuditTableTextAsync() => string.Join("\n",
            (await AuditAsync()).Select(row =>
                string.Join("|", row.Action, row.OperatorSource, row.OperatorId, row.OperatorDisplayName,
                    row.TargetType, row.TargetId, row.Outcome.ToString(CultureInfo.InvariantCulture),
                    row.ClientIp, row.CorrelationId, row.SecurityDescription, row.MetadataJson,
                    row.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture))));

        public async Task<int> SessionCountAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM admin_session";
            return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
        }

        public async Task<int> AuditCountAsync(string action)
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM service_audit_logs WHERE action = $action";
            command.Parameters.AddWithValue("$action", action);
            return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
            Base.Dispose();
            _rsa.Dispose();
        }

        private sealed class StubLogoutHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private sealed class Handler(Fixture f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                    .Select(p => p.Split('=', 2))
                    .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
                f.Entered.TrySetResult();
                if (f.Gate is not null) await f.Gate.Task.WaitAsync(cancellationToken);
                if (f.Defect == "network") throw new HttpRequestException(Code);
                var challenge = WebEncoders.Base64UrlEncode(
                    SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
                var nonce = f.Challenges[challenge];
                var access = f.Claims(f.Defect == "nonadmin" ? "student" : "admin");
                var id = f.Claims();
                id.Remove("role");
                id["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
                id["name"] = "id-user";
                id["nonce"] = f.Defect == "nonce" ? "wrong" : nonce;
                var accessToken = f.Token("at+jwt", access);
                var idToken = f.Token("JWT", id);
                f.LastAccess = accessToken;
                f.LastId = idToken;
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object>
                    {
                        ["access_token"] = accessToken,
                        ["id_token"] = idToken,
                        ["token_type"] = "Bearer",
                        ["expires_in"] = 900,
                        ["scope"] = "openid profile"
                    })
                };
            }
        }

        public Dictionary<string, object> Claims(string? role = null) => new()
        {
            ["iss"] = Issuer,
            ["aud"] = "client-id",
            ["sub"] = "account-42",
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60,
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 900,
            ["name"] = "access-user",
            ["role"] = role ?? "admin"
        };

        public string Token(string type, Dictionary<string, object> claims)
        {
            var header = new Dictionary<string, object> { ["alg"] = "RS256", ["kid"] = "key-1", ["typ"] = type };
            var signing = Base64UrlEncoder.Encode(JsonSerializer.Serialize(header)) + "." +
                Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims));
            var signature = _rsa.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return signing + "." + Base64UrlEncoder.Encode(signature);
        }

        private sealed class Metadata(SecurityKey key) : IConfigurationManager<OpenIdConnectConfiguration>
        {
            public SecurityKey Key { get; } = key;
            public OpenIdConnectConfiguration Configuration { get; } = Published(key);

            private static OpenIdConnectConfiguration Published(SecurityKey publishedKey)
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Fixture.Issuer,
                    AuthorizationEndpoint = Fixture.Issuer + "/authorize",
                    TokenEndpoint = Fixture.Issuer + "/token",
                    JwksUri = Fixture.Issuer + "/jwks"
                };
                configuration.SigningKeys.Add(publishedKey);
                return configuration;
            }

            public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
            {
                cancel.ThrowIfCancellationRequested();
                return Task.FromResult(Configuration);
            }

            public void RequestRefresh() { }
        }
    }
}
