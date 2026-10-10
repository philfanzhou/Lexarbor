using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Domain.Repositories;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The ServiceMantle security response-header baseline over the whole routed
/// /admin surface: authentication routes, the system version endpoint, business
/// administration endpoints and the unknown-route catch-all, on success,
/// validation, redirect, 401/403/404, 429 and exception-generated answers —
/// while /api, /health and the SPA never match the requirement.
/// </summary>
public class AdminSecurityResponseHeaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BaselineCsp =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private static void AssertBaseline(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-cache", string.Join(",", response.Headers.GetValues("Pragma")));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(BaselineCsp, response.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static void AssertNoBaseline(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains("Cache-Control"));
        Assert.False(response.Headers.Contains("Pragma"));
        Assert.False(response.Headers.Contains("X-Content-Type-Options"));
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.False(response.Headers.Contains("Referrer-Policy"));
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    private static HttpClient AdminClient(VocabularyWebApplicationFactory factory) =>
        WithNoRedirects(factory, factory.CreateToken("admin"));

    private static HttpClient WithNoRedirects(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string? token = null)
    {
        // The redirect answers under test are 302s into the SPA; following them
        // would land on the fallback and measure the wrong response.
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Theory]
    [InlineData("/admin/auth/session", "GET", HttpStatusCode.Unauthorized)]
    [InlineData("/admin/auth/session/", "GET", HttpStatusCode.Unauthorized)]
    [InlineData("/ADMIN/AUTH/SESSION", "GET", HttpStatusCode.Unauthorized)]
    [InlineData("/admin/auth/logout", "POST", HttpStatusCode.BadRequest)]
    [InlineData("/Admin/Auth/Logout/", "POST", HttpStatusCode.BadRequest)]
    // The anonymous return route never demands a caller: it answers with its
    // fixed failure redirect even for an anonymous request.
    [InlineData("/admin/auth/logout/return", "GET", HttpStatusCode.Redirect)]
    [InlineData("/ADMIN/AUTH/LOGOUT/RETURN/", "GET", HttpStatusCode.Redirect)]
    public async Task AuthenticationRoutes_CarryBaselineOnSuccessAndRedirects(
        string path, string method, HttpStatusCode anonymousStatus)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var admin = AdminClient(factory);
        using var anonymous = WithNoRedirects(factory);

        using var anonymousAnswer = method == "GET"
            ? await anonymous.GetAsync(path, Ct)
            : await anonymous.PostAsync(path, null, Ct);
        Assert.Equal(anonymousStatus, anonymousAnswer.StatusCode);
        AssertBaseline(anonymousAnswer);

        using var answered = method == "GET"
            ? await admin.GetAsync(path, Ct)
            : await admin.PostAsync(path, null, Ct);
        // The session route answers 200 for the administrator; the logout endpoint
        // answers its antiforgery rejection for a caller without a token pair, and
        // the fixed return route always redirects with its failure target when no
        // logout is pending.
        Assert.True(answered.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.BadRequest,
            $"unexpected status {answered.StatusCode}");
        AssertBaseline(answered);
    }

    [Theory]
    [InlineData("/admin/system/version")]
    [InlineData("/admin/system/version/")]
    [InlineData("/ADMIN/system/version/")]
    public async Task SystemVersion_CarriesBaselineWithoutValidators(string path)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var admin = AdminClient(factory);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", "*");
        admin.DefaultRequestHeaders.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(1);

        using var response = await admin.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertBaseline(response);
        // The handwritten no-store middleware also stripped caching validators;
        // the baseline keeps that guarantee.
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task StartAndCallback_CarryBaselineOnEveryAnswerableFailure()
    {
        // Without hosted-login configuration the start route refuses with 503 and
        // the callback round trip with its fixed redirect; the rejected return
        // target answers 400 before any configuration is even read.
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = WithNoRedirects(factory);

        // The optional-login degradation answers every start with its fixed 503
        // before the return target is even read — the package's ordering.
        using var badTarget = await client.GetAsync("/admin/auth/start?returnUrl=https%3A%2F%2Fevil.test", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, badTarget.StatusCode);
        AssertBaseline(badTarget);

        using var unconfigured = await client.GetAsync("/admin/auth/start", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unconfigured.StatusCode);
        AssertBaseline(unconfigured);

        using var callback = await client.GetAsync(
            "/admin/auth/callback?code=synthetic-code&state=synthetic-state&iss=untrusted", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        AssertBaseline(callback);
    }

    [Theory]
    [InlineData("/admin/auth/start//", HttpStatusCode.Unauthorized)]
    [InlineData("/admin/no-such-admin-route", HttpStatusCode.Unauthorized)]
    [InlineData("/ADMIN/No_Such_Route/", HttpStatusCode.Unauthorized)]
    public async Task UnknownAdminRoutes_CarryBaselineOnTheCatchall401And404(string path, HttpStatusCode anonymousStatus)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var anonymous = WithNoRedirects(factory);
        using var admin = AdminClient(factory);

        using var rejected = await anonymous.GetAsync(path, Ct);
        Assert.Equal(anonymousStatus, rejected.StatusCode);
        AssertBaseline(rejected);

        using var notFound = await admin.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        AssertBaseline(notFound);
    }

    [Fact]
    public async Task BusinessAdminEndpoints_CarryBaselineOnReadWriteAndValidation()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var admin = AdminClient(factory);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var read = await admin.GetAsync("/admin/vocabulary-books?page=1&size=20", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        AssertBaseline(read);

        using var created = await admin.PostAsJsonAsync(
            "/admin/vocabulary-books",
            new { bookName = $"Header Baseline {suffix}", status = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        AssertBaseline(created);

        using var invalid = await admin.PostAsJsonAsync(
            "/admin/vocabulary-books", new { bookName = " ", status = true }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        AssertBaseline(invalid);

        using var batchValidation = await admin.PostAsJsonAsync(
            "/admin/vocabulary/batch",
            new { bookId = "missing-book", entries = new[] { new { word = " ", meaning = "x" } } }, Ct);
        Assert.True(batchValidation.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"unexpected status {batchValidation.StatusCode}");
        AssertBaseline(batchValidation);
    }

    [Fact]
    public async Task CookieWritesWithoutCsrf_CarryBaselineOnThe401()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var cookieClient = WithNoRedirects(factory);
        cookieClient.DefaultRequestHeaders.Add(
            "Cookie", factory.CreateSessionCookie("admin"));

        using var rejected = await cookieClient.PostAsJsonAsync(
            "/admin/vocabulary-books", new { bookName = "csrf", status = true }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        AssertBaseline(rejected);
    }

    [Fact]
    public async Task ExceptionGeneratedAdminFailure_CarriesBaselineOnThe500()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var token = factory.CreateToken("admin");
        // Seed the book directly: only the vocabulary repository is replaced
        // below, and the batch import through it is the 500 under test.
        var bookId = $"baseline-{suffix}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<Lexarbor.Database.VocabularyDbContext>();
            context.VocabularyBooks.Add(new Lexarbor.Database.Entities.VocabularyBookEntity
            {
                Id = bookId,
                BookName = $"Baseline 500 {suffix}",
                Status = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(Ct);
        }
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVocabularyRepository>();
            services.AddScoped<IVocabularyRepository>(_ => new ThrowingRepository());
        }));
        using var admin = WithNoRedirects(configured, token);

        using var response = await admin.PostAsJsonAsync(
            "/admin/vocabulary/batch",
            new { bookId, entries = new[] { new { word = $"alpha{suffix}", meaning = "one" } } }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertBaseline(response);
    }

    [Fact]
    public async Task RateLimitedAdminStart_429CarriesBaseline()
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["RateLimits:AdminLogin:Enabled"] = "true",
                ["RateLimits:AdminLogin:PermitLimit"] = "2",
                ["RateLimits:AdminLogin:WindowSeconds"] = "300"
            });
        using var client = WithNoRedirects(factory);
        client.DefaultRequestHeaders.Add(VocabularyWebApplicationFactory.ClientAddressHeader, "203.0.113.40");

        using var first = await client.GetAsync("/admin/auth/start", Ct);
        using var second = await client.GetAsync("/admin/auth/start", Ct);
        AssertBaseline(first);
        AssertBaseline(second);

        using var limited = await client.GetAsync("/admin/auth/start", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        AssertBaseline(limited);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/api/vocabulary?page=1&size=20")]
    [InlineData("/api/vocabulary-books/all")]
    [InlineData("/api/no-such-api-route")]
    [InlineData("/")]
    [InlineData("/no-such-spa-route")]
    public async Task PublicSurfaces_NeverCarryTheBaseline(string path)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        // Whatever the public surface answers — 200, 404, or the SPA fallback —
        // it never carries the administration-only header baseline.
        AssertNoBaseline(response);
    }

    private sealed class ThrowingRepository : IVocabularyRepository
    {
        public Task<(List<Lexarbor.Domain.Models.VocabularyModel> Items, int TotalCount)> SearchAsync(
            string? keyword, int page, int size)
            => throw new InvalidOperationException("header-baseline injected failure");

        public Task<Lexarbor.Domain.Models.VocabularyModel?> GetByIdAsync(string id) => throw Unused();
        public Task<Lexarbor.Domain.Models.VocabularyModel?> GetByWordAsync(string word) => throw Unused();
        public Task<Lexarbor.Domain.Models.VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord) => throw Unused();
        public Task<List<Lexarbor.Domain.Models.VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids) => throw Unused();
        public Task AddAsync(Lexarbor.Domain.Models.VocabularyModel model) => throw Unused();
        public Task UpdateAsync(Lexarbor.Domain.Models.VocabularyModel model) => throw Unused();
        public Task<List<Lexarbor.Domain.Models.VocabularyModel>> GetRandomByBookExceptAsync(
            string bookId, string excludeVocabularyId, string excludeWord,
            string excludeEquivalentMeaning, int count,
            Lexarbor.Domain.Repositories.VocabularyDistractorScope? scope = null) => throw Unused();

        private static NotSupportedException Unused() => new();
    }
}
