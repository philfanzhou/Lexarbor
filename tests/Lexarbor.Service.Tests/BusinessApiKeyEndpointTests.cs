using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

public sealed class BusinessApiKeyEndpointTests
{
    private const string Management = "/admin/api-keys";
    private const string Catalog = "/api/vocabulary-books/all";
    private const string Submissions = "/integration/meaning-submissions";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryBusinessRouteAuthorizesBeforeValidation()
    {
        await using var host = new VocabularyWebApplicationFactory();
        using var client = host.CreateClient();
        var catalogKey = await host.SeedKeyAsync();
        var submissionKey = await host.SeedKeyAsync("study", catalog: false, meaningSubmissions: true);
        var calls = new (HttpMethod Method, string Path, bool Catalog)[]
        {
            (HttpMethod.Get, "/api/vocabulary/missing", true), (HttpMethod.Get, "/api/vocabulary", true),
            (HttpMethod.Post, "/api/vocabulary/question", true), (HttpMethod.Post, "/api/vocabulary/questions", true),
            (HttpMethod.Post, "/api/vocabulary/learning-question", true), (HttpMethod.Get, Catalog, true),
            (HttpMethod.Get, "/api/vocabulary-books/missing/units", true), (HttpMethod.Get, "/api/vocabulary-books/missing/entries", true),
            (HttpMethod.Get, "/api/vocabulary/missing/audio", true),
            (HttpMethod.Get, "/integration/vocabulary/search", false), (HttpMethod.Post, Submissions, false),
            (HttpMethod.Post, Submissions + "/lookup", false), (HttpMethod.Post, Submissions + "/id/question", false),
            (HttpMethod.Post, Submissions + "/id/learning-question", false)
        };
        var cookie = host.CreateSessionCookie();
        var admin = host.CreateToken("admin");
        using var rsa = RSA.Create(2048);
        var now = DateTime.UtcNow;
        var legacy = new JwtSecurityToken("urn:test:study", "urn:lexarbor:integration",
            [new("sub", "study"), new("scope", "meaning-submissions"), new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)],
            now, now.AddSeconds(60), new(new RsaSecurityKey(rsa) { KeyId = "legacy" }, SecurityAlgorithms.RsaSha256));
        var legacyToken = new JwtSecurityTokenHandler().WriteToken(legacy);
        foreach (var (method, path, catalog) in calls)
        {
            await Failure(await Send(client, method, path), HttpStatusCode.Unauthorized);
            await Failure(await Send(client, method, path, malformed: true), HttpStatusCode.Unauthorized);
            await Failure(await Send(client, method, path, key: "invalid", malformed: true), HttpStatusCode.Unauthorized);
            await Failure(await Send(client, method, path, key: catalog ? submissionKey : catalogKey, malformed: true), HttpStatusCode.Forbidden);
            await Failure(await Send(client, method, path, bearer: admin, malformed: true), HttpStatusCode.Unauthorized);
            await Failure(await Send(client, method, path, bearer: legacyToken, malformed: true), HttpStatusCode.Unauthorized);
            await Failure(await Send(client, method, path, cookie: cookie, malformed: true), HttpStatusCode.Unauthorized);
        }
        using var malformedHeaders = new HttpRequestMessage(HttpMethod.Get, Catalog);
        malformedHeaders.Headers.Add(BusinessApiAuthorization.Header, [catalogKey, catalogKey]);
        await Failure(await client.SendAsync(malformedHeaders, Ct), HttpStatusCode.Unauthorized);
        await Failure(await client.GetAsync(Catalog + "?apiKey=" + "synthetic-query-credential", Ct), HttpStatusCode.Unauthorized);
        using var valid = await Send(client, HttpMethod.Get, Catalog, key: catalogKey);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        await Failure(await Send(client, HttpMethod.Get, Management, key: catalogKey), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IssueListAndRevokeRequireAdminAndSessionWritesRequireCsrf()
    {
        await using var host = new VocabularyWebApplicationFactory();
        using var client = host.CreateClient();
        var adminBearer = host.CreateToken("admin");
        var ordinaryBearer = host.CreateToken("student");
        foreach (var (method, path) in new[] { (HttpMethod.Get, Management), (HttpMethod.Post, Management), (HttpMethod.Delete, Management + "/missing") })
        {
            await Failure(await Send(client, method, path, malformed: true), HttpStatusCode.Unauthorized);
            await Failure(await Send(client, method, path, bearer: ordinaryBearer, malformed: true), HttpStatusCode.Forbidden);
        }
        using var session = host.CreateClient();
        session.DefaultRequestHeaders.Add("Cookie", host.CreateSessionCookie());
        using var listing = await session.GetAsync(Management, Ct);
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        await Failure(await Send(session, HttpMethod.Post, Management, body: Request("study", "catalog")), HttpStatusCode.Unauthorized);
        AdminTestAntiforgery.Attach(session, host);
        using var made = await Send(session, HttpMethod.Post, Management, body: Request("study", "catalog"));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.Equal("no-store", made.Headers.CacheControl?.ToString());
        var id = (await ReadData(made)).GetProperty("metadata").GetProperty("id").GetString();
        using var revoked = await session.DeleteAsync(Management + "/" + id, Ct);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        foreach (var invalid in new[] { new { callerId = "with whitespace", scopes = new[] { "catalog" } }, Request("", "catalog"), Request("study"), Request("study", "other"), Request("study", "catalog", "catalog") })
            await Failure(await Send(client, HttpMethod.Post, Management, bearer: adminBearer, body: invalid), HttpStatusCode.BadRequest);
        await Failure(await Send(client, HttpMethod.Post, Management, bearer: adminBearer, malformed: true), HttpStatusCode.BadRequest);
        await Failure(await Send(client, HttpMethod.Delete, Management + "/missing", bearer: adminBearer), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminBookPickerReturnsAllEnabledBooksWithTheSession()
    {
        const string picker = "/admin/vocabulary-books/active";
        await using var host = new VocabularyWebApplicationFactory();
        using var client = host.CreateClient();
        var key = await host.SeedKeyAsync();
        await Failure(await Send(client, HttpMethod.Get, picker, key: key), HttpStatusCode.Unauthorized);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            for (var index = 0; index < 22; index++)
                db.VocabularyBooks.Add(new() { Id = $"book-{index}", BookName = $"Book {index}", Status = index < 21 });
            await db.SaveChangesAsync(Ct);
        }
        using var response = await Send(client, HttpMethod.Get, picker, cookie: host.CreateSessionCookie());
        var books = (await ReadData(response)).GetProperty("books").EnumerateArray().ToArray();
        Assert.Equal(21, books.Length);
        Assert.DoesNotContain(books, book => book.GetProperty("id").GetString() == "book-21");
    }

    [Fact]
    public async Task KeysPersistAcrossRestartAndRotationPreservesHistoricalSubmissionSource()
    {
        var root = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"api-key-lifecycle-{Guid.NewGuid():N}");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var logs = new CaptureLogs();
        try
        {
            var settings = new Dictionary<string, string?> { ["MeaningSubmissions:BookId"] = "supplement" };
            VocabularyWebApplicationFactory Create() => new("Testing", true, extraConfiguration: settings, keyContentRoot: root,
                databasePath: Path.Combine(root, "vocabulary.db"));
            string oldKey, oldId, activeKey, oldSubmissionId;
            await using (var factory = Create())
            await using (var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs))))
            using (var client = host.CreateHttpsClient())
            {
                var admin = factory.CreateToken("admin");
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                db.VocabularyBooks.Add(new() { Id = "supplement", BookName = "Supplement", Status = true });
                db.MeaningSubmissions.Add(new() { Id = "legacy", Source = "study.original", Word = "historical", Meaning = "Earlier submission" });
                await db.SaveChangesAsync(Ct);
                oldSubmissionId = "legacy";
                (oldKey, oldId) = await Issue(client, admin);
                await Failure(await Send(client, HttpMethod.Post, Management, bearer: admin,
                    body: Request("other.integration", "meaning-submissions")), HttpStatusCode.Conflict);
                (activeKey, _) = await Issue(client, admin);
                Assert.True(activeKey != oldKey);
                using var oldResponse = await Send(client, HttpMethod.Get, Catalog, key: oldKey);
                Assert.Equal(HttpStatusCode.OK, oldResponse.StatusCode);
                var persisted = await db.BusinessApiKeys.AsNoTracking().ToArrayAsync(Ct);
                Assert.Equal(2, persisted.Length);
                Assert.All(persisted, row => Assert.Equal(64, row.SecretHash.Length));
                Assert.Contains(persisted, row => row.SecretHash == BusinessApiKeyAuthentication.HashKey(activeKey));
                for (var i = 0; i < 2; i++)
                {
                    using var revoked = await Send(client, HttpMethod.Delete, Management + "/" + oldId, bearer: admin);
                    Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
                }
            }

            await using var restarted = Create();
            using var connected = restarted.CreateClient();
            await Failure(await Send(connected, HttpMethod.Get, Catalog, key: oldKey), HttpStatusCode.Unauthorized);
            await Failure(await Send(connected, HttpMethod.Post, Submissions, key: oldKey), HttpStatusCode.Unauthorized);
            using var active = await Send(connected, HttpMethod.Get, Catalog, key: activeKey);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
            using var list = await Send(connected, HttpMethod.Get, Management, bearer: restarted.CreateToken("admin"));
            var raw = await list.Content.ReadAsStringAsync(Ct);
            Assert.True(!raw.Contains(activeKey, StringComparison.Ordinal) && !raw.Contains(oldKey, StringComparison.Ordinal));
            var metadata = (await ReadData(list)).GetProperty("items").EnumerateArray();
            Assert.All(metadata, item => Assert.Equal("study.original", item.GetProperty("callerId").GetString()));
            Assert.Single(metadata, item => item.GetProperty("revokedAtUnixMs").ValueKind == JsonValueKind.Number);
            var lookedUp = await ReadData(await Send(connected, HttpMethod.Post, Submissions + "/lookup", key: activeKey, body: new { ids = new[] { oldSubmissionId } }));
            Assert.Equal(oldSubmissionId, Assert.Single(lookedUp.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
            var question = await ReadData(await Send(connected, HttpMethod.Post, Submissions + "/legacy/learning-question", key: activeKey, body: new { mode = "review" }));
            Assert.Equal("FillLetters", question.GetProperty("type").GetString());
            var submission = await ReadData(await Send(connected, HttpMethod.Post, Submissions, key: activeKey,
                body: new { word = "rotated", meaning = "New submission" }));
            using var after = restarted.Services.CreateScope();
            var stored = after.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var submissionId = submission.GetProperty("id").GetString();
            Assert.Equal("study.original", await stored.MeaningSubmissions.Where(item => item.Id == submissionId).Select(item => item.Source).SingleAsync(Ct));
            Assert.True(logs.Messages.All(message => !message.Contains(oldKey, StringComparison.Ordinal) && !message.Contains(activeKey, StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static object Request(string callerId, params string[] scopes) => new { callerId, scopes };

    private static async Task<(string Key, string Id)> Issue(HttpClient client, string admin)
    {
        using var response = await Send(client, HttpMethod.Post, Management, bearer: admin,
            body: Request("study.original", "catalog", "meaning-submissions"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var data = await ReadData(response);
        return (data.GetProperty("key").GetString()!, data.GetProperty("metadata").GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> ReadData(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("data").Clone();
    }

    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(new[] { "message", "success" }, json.RootElement.EnumerateObject().Select(property => property.Name).Order());
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        }
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path,
        string? key = null, string? bearer = null, string? cookie = null, object? body = null, bool malformed = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (key != null) request.Headers.Add(BusinessApiAuthorization.Header, key);
        if (bearer != null) request.Headers.Authorization = new("Bearer", bearer);
        if (cookie != null) request.Headers.Add("Cookie", cookie);
        if (method == HttpMethod.Post && malformed) request.Content = new StringContent("{", Encoding.UTF8, "application/json");
        if (body != null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, Ct);
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) =>
                messages.Enqueue(format(state, error));
        }
    }
}
