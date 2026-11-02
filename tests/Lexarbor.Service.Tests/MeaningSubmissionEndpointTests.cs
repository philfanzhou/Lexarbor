using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

public sealed partial class MeaningSubmissionEndpointTests : IAsyncLifetime
{
    private const string Issuer = "urn:test:student";
    private const string Subject = "test.student";
    private const string Audience = "urn:lexarbor:integration";
    private const string KeyId = "student-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _keyDirectory = Directory.CreateTempSubdirectory("lexarbor-integration-").FullName;
    private VocabularyWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var publicKeyPath = Path.Combine(_keyDirectory, "student.pub.pem");
        await File.WriteAllTextAsync(publicKeyPath, _key.ExportSubjectPublicKeyInfoPem(), Ct);
        _factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration: new Dictionary<string, string?>
        {
            ["MeaningSubmissions:BookId"] = "supplementary",
            ["MeaningSubmissions:Integration:Issuer"] = Issuer,
            ["MeaningSubmissions:Integration:Subject"] = Subject,
            ["MeaningSubmissions:Integration:PublicKeys:0:KeyId"] = KeyId,
            ["MeaningSubmissions:Integration:PublicKeys:0:PublicKeyPath"] = publicKeyPath
        });
        _client = _factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(
            new() { Id = "supplementary", BookName = "Supplementary", Status = true },
            new() { Id = "main", BookName = "Main", Status = true });
        foreach (var (id, word, meaning) in new[] { ("w1", "apple", "苹果"), ("w2", "river", "河流"), ("w3", "cloud", "云"), ("w4", "stone", "石头") })
        {
            db.Vocabularies.Add(new() { Id = id, Word = word });
            db.VocabularyMeanings.Add(new() { Id = "m" + id, VocabularyId = id, BookId = "main", Meaning = meaning });
        }

        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _key.Dispose();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    private string Token(string scope = "meaning-submissions", int lifetimeSeconds = 60, string subject = Subject, string kid = KeyId)
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = new JwtSecurityToken(Issuer, Audience,
            [new Claim("sub", subject), new Claim("scope", scope),
                new Claim("iat", issued.ToString(), ClaimValueTypes.Integer64)],
            DateTimeOffset.FromUnixTimeSeconds(issued).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(issued + lifetimeSeconds).UtcDateTime,
            new SigningCredentials(new RsaSecurityKey(_key) { KeyId = kid }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<HttpResponseMessage> Post(string path, object body, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer != null) request.Headers.Authorization = new("Bearer", bearer);
        return await _client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task IntegrationRoutes_RequireAValidServiceTokenWithTheScope()
    {
        var body = new { word = "serendipity", meaning = "意外发现" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/integration/meaning-submissions", body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, _factory.CreateToken("admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, Token(lifetimeSeconds: 120))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, Token(subject: "someone.else"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, Token(kid: "unknown"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post("/integration/meaning-submissions", body, Token(scope: "other"))).StatusCode);

        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/meaning-submissions");
        adminRequest.Headers.Authorization = new("Bearer", Token());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(adminRequest, Ct)).StatusCode);

        var unknown = await Post("/integration/unknown", body, Token());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task IntegrationToken_CannotReadOrWriteEnrichmentSettings()
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, "/admin/enrichment/settings");
            request.Headers.Authorization = new("Bearer", Token());
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new { enrichment = new { audioEnabled = true } });
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task IntegrationToken_CannotUseEnrichmentTasksOrRetry()
    {
        foreach (var path in new[] { "/admin/enrichment/tasks", "/admin/enrichment/tasks/unknown", "/admin/enrichment/summary" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new("Bearer", Token());
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request, Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/admin/enrichment/tasks/unknown/retry",
            new { expectedRound = 1, retryKey = Guid.NewGuid().ToString() }, Token())).StatusCode);
    }

    [Fact]
    public async Task CatalogSearch_ProjectsMeaningPronunciationAndExcludesDisabledBooks()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var word = await db.Vocabularies.SingleAsync(row => row.Id == "w1", Ct);
            word.PhoneticUk = "/default/";
            word.PhoneticUs = " ";
            var meaning = await db.VocabularyMeanings.SingleAsync(row => row.Id == "mw1", Ct);
            meaning.PhoneticUk = "/specific/";
            meaning.PhoneticUs = "\t";
            db.VocabularyBooks.Add(new() { Id = "disabled", BookName = "Disabled", Status = false });
            db.VocabularyMeanings.Add(new()
            {
                Id = "hidden",
                VocabularyId = "w1",
                BookId = "disabled",
                Meaning = "hidden",
                PhoneticUk = "/hidden/"
            });
            await db.SaveChangesAsync(Ct);
        }
        using var search = new HttpRequestMessage(HttpMethod.Get, "/integration/vocabulary/search?keyword=apple");
        search.Headers.Authorization = new("Bearer", Token());
        using var response = await _client.SendAsync(search, Ct);
        var matches = await Data(response);
        var match = Assert.Single(matches.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "bookId", "bookName", "meaning", "meaningId", "meaningKey", "partOfSpeech",
                "phoneticUk", "phoneticUs", "pronunciation", "word", "wordId", "wordKey" },
            match.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("/default/", match.GetProperty("phoneticUk").GetString());
        Assert.Equal(" ", match.GetProperty("phoneticUs").GetString());
        var pronunciation = match.GetProperty("pronunciation");
        Assert.Equal("/specific/", pronunciation.GetProperty("uk").GetProperty("phonetic").GetString());
        Assert.Equal("meaning", pronunciation.GetProperty("uk").GetProperty("source").GetString());
        Assert.Null(pronunciation.GetProperty("us").GetProperty("phonetic").GetString());
        Assert.Equal("default", pronunciation.GetProperty("us").GetProperty("source").GetString());
    }

    [Fact]
    public async Task SubmitReviewAndLookup_RoundTrip()
    {
        var created = await Data(await Post("/integration/meaning-submissions",
            new { word = "serendipity", partOfSpeech = "n.", meaning = "意外发现" }, Token()));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("pending", created.GetProperty("status").GetString());

        var question = await Data(await Post($"/integration/meaning-submissions/{id}/question",
            new { chineseToEnglish = false }, Token()));
        Assert.Equal("serendipity", question.GetProperty("word").GetString());
        Assert.Equal(4, question.GetProperty("options").GetArrayLength());

        using var admin = new HttpRequestMessage(HttpMethod.Get, "/admin/meaning-submissions?status=pending");
        admin.Headers.Authorization = new("Bearer", _factory.CreateToken("admin"));
        var page = await Data(await _client.SendAsync(admin, Ct));
        Assert.True(page.GetProperty("configured").GetBoolean());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(Subject, item.GetProperty("source").GetString());

        var approved = await Data(await Post($"/admin/meaning-submissions/{id}/approve",
            new { word = "serendipity", partOfSpeech = "n.", meaning = "意外发现" }, _factory.CreateToken("admin")));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.True(approved.GetProperty("definitionKept").GetBoolean());

        var lookup = await Data(await Post("/integration/meaning-submissions/lookup", new { ids = new[] { id, "missing" } }, Token()));
        var found = Assert.Single(lookup.GetProperty("items").EnumerateArray());
        Assert.Equal("supplementary", found.GetProperty("resultBookId").GetString());

        using var search = new HttpRequestMessage(HttpMethod.Get, "/integration/vocabulary/search?keyword=Seren");
        search.Headers.Authorization = new("Bearer", Token());
        var matches = await Data(await _client.SendAsync(search, Ct));
        var match = Assert.Single(matches.GetProperty("items").EnumerateArray());
        Assert.Equal("supplementary", match.GetProperty("bookId").GetString());
        Assert.Equal("意外发现", match.GetProperty("meaningKey").GetString());

        var advice = await Post($"/admin/meaning-submissions/{id}/advice", new { }, _factory.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, advice.StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewComments_Compatibility_Projection_AndSource(bool reject)
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "testword", meaning = "original" }, Token()));
        var id = pending.GetProperty("id").GetString()!;
        Assert.Null(pending.GetProperty("reviewComment").GetString());
        var adminToken = _factory.CreateToken("admin");
        var reviewed = reject
            ? await Data(await Post($"/admin/meaning-submissions/{id}/reject", new { reviewComment = "  reason  " }, adminToken))
            : await Data(await Post($"/admin/meaning-submissions/{id}/approve", new { word = "testword", meaning = "corrected", reviewComment = "  reason  " }, adminToken));
        Assert.Equal("reason", reviewed.GetProperty("reviewComment").GetString());
        var lookup = await Data(await Post("/integration/meaning-submissions/lookup", new { ids = new[] { id } }, Token()));
        Assert.Equal("reason", lookup.GetProperty("items")[0].GetProperty("reviewComment").GetString());
        using var read = new HttpRequestMessage(HttpMethod.Get, $"/admin/meaning-submissions?status={(reject ? "rejected" : "approved")}");
        read.Headers.Authorization = new("Bearer", adminToken);
        var item = Assert.Single((await Data(await _client.SendAsync(read, Ct))).GetProperty("items").EnumerateArray());
        Assert.Equal("admin", item.GetProperty("reviewerKind").GetString());
        Assert.Equal(Subject, item.GetProperty("source").GetString());
        Assert.Equal("reason", item.GetProperty("submission").GetProperty("reviewComment").GetString());
        var again = await Data(await Post("/integration/meaning-submissions", new { word = "anotherword", meaning = "original" }, Token()));
        var next = again.GetProperty("id").GetString()!;
        if (reject)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/meaning-submissions/{next}/reject");
            request.Headers.Authorization = new("Bearer", adminToken);
            var oldResult = await Data(await _client.SendAsync(request, Ct));
            Assert.Null(oldResult.GetProperty("reviewComment").GetString());
        }
        else
        {
            var ordinary = await Data(await Post($"/admin/meaning-submissions/{next}/approve", new { word = "anotherword", meaning = "original", reviewComment = "ignored for kept" }, adminToken));
            Assert.True(ordinary.GetProperty("definitionKept").GetBoolean());
            Assert.Null(ordinary.GetProperty("reviewComment").GetString());
            var reused = await Data(await Post("/integration/meaning-submissions", new { word = "anotherword", meaning = "original" }, Token()));
            Assert.Equal(next, reused.GetProperty("id").GetString()); Assert.Null(reused.GetProperty("reviewComment").GetString());
        }
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task ReviewWrites_ValidateBoundsAndAuthentication(string action)
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "testword", meaning = "original" }, Token()));
        var id = pending.GetProperty("id").GetString()!;
        var path = $"/admin/meaning-submissions/{id}/{action}";
        var body = new { word = "testword", meaning = "changed", reviewComment = "sensitive synthetic reason" };
        foreach (var (token, status) in new[] { ((string?)null, HttpStatusCode.Unauthorized), (Token(), HttpStatusCode.Unauthorized), (_factory.CreateToken("student"), HttpStatusCode.Forbidden) })
            Assert.Equal(status, (await Post(path, body, token)).StatusCode);
        using (var cookie = _factory.CreateClient())
        {
            cookie.DefaultRequestHeaders.Add("Cookie", _factory.CreateSessionCookie());
            using var blocked = await cookie.PostAsJsonAsync(path, body, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
            AdminTestAntiforgery.Attach(cookie, _factory);
            using var invalid = await cookie.PostAsJsonAsync(path, new { word = "testword", meaning = "changed", reviewComment = new string('x', 501) }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain(new string('x', 501), await invalid.Content.ReadAsStringAsync(Ct));
            using var valid = await cookie.PostAsJsonAsync(path, body, Ct);
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        }
    }

    private async Task<JsonElement> ReadAdmin(string path, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", token ?? _factory.CreateToken("admin"));
        return await Data(await _client.SendAsync(request, Ct));
    }

    [Fact]
    public async Task PermanentHistory_FiltersProjection_AndBodylessIdempotentChecks()
    {
        string recordId;
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MeaningSubmissionService>();
            var submission = await service.SubmitAsync(Subject, "historyword", "n.", "original", "example", Ct);
            var record = await service.ApplyAiReviewAsync(new("history-attempt", submission.Id, "revise", "v.", "original",
                "/uk/", "/us/", "<img src=x onerror=alert(1)>", "actual-model", DateTimeOffset.FromUnixTimeMilliseconds(1234)), new AllowReview(), Ct);
            recordId = record.Id;
            // This second valid conclusion lost the race; it must still be queryable, never labelled applied.
            await service.ApplyAiReviewAsync(record.Review with { AttemptId = "late", Model = "captured-earlier" }, new AllowReview(), Ct);
        }
        var all = await ReadAdmin("/admin/meaning-submission-reviews");
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
        var applied = await ReadAdmin("/admin/meaning-submission-reviews?verdict=revise&applied=true&checked=false");
        var row = Assert.Single(applied.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "ai", "applied", "checked", "checkedAt", "original", "recordId", "resultWordId", "reviewerKind", "source", "submissionId" }, row.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("ai", row.GetProperty("reviewerKind").GetString()); Assert.Equal(Subject, row.GetProperty("source").GetString());
        Assert.Equal("historyword", row.GetProperty("original").GetProperty("word").GetString());
        Assert.Equal("n.", row.GetProperty("original").GetProperty("partOfSpeech").GetString());
        Assert.Equal("original", row.GetProperty("original").GetProperty("meaning").GetString());
        Assert.Equal("example", row.GetProperty("original").GetProperty("example").GetString());
        var ai = row.GetProperty("ai"); Assert.Equal("actual-model", ai.GetProperty("model").GetString());
        Assert.Equal("v.", ai.GetProperty("partOfSpeech").GetString()); Assert.Equal("/uk/", ai.GetProperty("phoneticUk").GetString());
        Assert.Equal("/us/", ai.GetProperty("phoneticUs").GetString()); Assert.Equal("<img src=x onerror=alert(1)>", ai.GetProperty("comment").GetString());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1234), ai.GetProperty("reviewedAt").GetDateTimeOffset());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/meaning-submission-reviews/{recordId}/checked");
        request.Headers.Authorization = new("Bearer", _factory.CreateToken("admin"));
        var first = await Data(await _client.SendAsync(request, Ct));
        var repeat = await Data(await Post($"/admin/meaning-submission-reviews/{recordId}/checked", new { }, _factory.CreateToken("admin")));
        Assert.Equal(first.GetRawText(), repeat.GetRawText());
        var confirmed = await ReadAdmin($"/admin/meaning-submission-reviews?recordId={recordId}");
        Assert.True(Assert.Single(confirmed.GetProperty("items").EnumerateArray()).GetProperty("checked").GetBoolean());
        Assert.Equal(1, (await ReadAdmin("/admin/meaning-submission-reviews?checked=true")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await ReadAdmin("/admin/meaning-submission-reviews?checked=false&applied=false")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await ReadAdmin("/admin/meaning-submissions?status=approved&reviewerKind=ai")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await ReadAdmin("/admin/meaning-submissions?status=approved&reviewerKind=admin")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await ReadAdmin("/admin/meaning-submissions?status=approved&reviewerKind=unknown")).GetProperty("totalCount").GetInt32());
        Assert.Empty((await ReadAdmin("/admin/meaning-submission-reviews?recordId=missing")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/admin/meaning-submission-reviews/missing/checked", new { }, _factory.CreateToken("admin"))).StatusCode);
    }

    [Fact]
    public async Task HistoryAndCheck_RequireAdministrator_CookieCsrfOrBearer_RejectIntegration()
    {
        foreach (var (token, expected) in new[] { ((string?)null, HttpStatusCode.Unauthorized), (Token(), HttpStatusCode.Unauthorized), (_factory.CreateToken("student"), HttpStatusCode.Forbidden) })
        {
            using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/meaning-submission-reviews");
            if (token != null) read.Headers.Authorization = new("Bearer", token);
            Assert.Equal(expected, (await _client.SendAsync(read, Ct)).StatusCode);
            Assert.Equal(expected, (await Post("/admin/meaning-submission-reviews/missing/checked", new { }, token)).StatusCode);
        }
        using var cookie = _factory.CreateClient(); cookie.DefaultRequestHeaders.Add("Cookie", _factory.CreateSessionCookie());
        Assert.Equal(HttpStatusCode.OK, (await cookie.GetAsync("/admin/meaning-submission-reviews", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cookie.PostAsync("/admin/meaning-submission-reviews/missing/checked", null, Ct)).StatusCode);
        AdminTestAntiforgery.Attach(cookie, _factory);
        Assert.Equal(HttpStatusCode.NotFound, (await cookie.PostAsync("/admin/meaning-submission-reviews/missing/checked", null, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/admin/meaning-submission-reviews?page=0")]
    [InlineData("/admin/meaning-submission-reviews?size=0")]
    [InlineData("/admin/meaning-submission-reviews?size=101")]
    [InlineData("/admin/meaning-submission-reviews?page=2147483647&size=100")]
    [InlineData("/admin/meaning-submission-reviews?verdict=unknown")]
    [InlineData("/admin/meaning-submission-reviews?checked=no")]
    [InlineData("/admin/meaning-submission-reviews?applied=no")]
    [InlineData("/admin/meaning-submission-reviews?recordId=%20")]
    [InlineData("/admin/meaning-submissions?reviewerKind=student")]
    public async Task HistoryFilters_RejectInvalidInput(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", _factory.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request, Ct)).StatusCode);
    }
    [Fact]
    public async Task ReviewerFilter_DistinguishesAllThreeKinds_WithoutChangingClientSource()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MeaningSubmissionService>();
            var human = await service.SubmitAsync("human-client", "humanword", null, "human original", null, Ct);
            await service.ApproveAsync(human.Id, new("humanword", null, "human corrected", null, null, null, "manual comment"), Ct);
            var ai = await service.SubmitAsync("ai-client", "aiword", null, "ai original", null, Ct);
            await service.ApplyAiReviewAsync(new("actual-attempt", ai.Id, "accept", null, null, null, null, null, "model", DateTimeOffset.UtcNow), new AllowReview(), Ct);
            // Preserve an unknown reviewer for a historical approved row without inventing an identity.
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var legacy = await service.SubmitAsync("legacy-client", "legacyword", null, "legacy", null, Ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE meaning_submission SET status=1 WHERE id={0}", [legacy.Id], Ct);
        }
        foreach (var (kind, source) in new[] { ("admin", "human-client"), ("ai", "ai-client"), ("unknown", "legacy-client") })
        {
            var result = await ReadAdmin($"/admin/meaning-submissions?status=approved&reviewerKind={kind}");
            var row = Assert.Single(result.GetProperty("items").EnumerateArray());
            Assert.Equal(source, row.GetProperty("source").GetString());
            Assert.Equal(kind == "unknown" ? null : kind, row.GetProperty("reviewerKind").GetString());
        }
        Assert.Equal(3, (await ReadAdmin("/admin/meaning-submissions?status=approved")).GetProperty("totalCount").GetInt32());
    }

    private sealed class AllowReview : IMeaningSubmissionReviewEligibility
    {
        public Task<bool> CanApplyAsync(string submissionId, string attemptId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

}
