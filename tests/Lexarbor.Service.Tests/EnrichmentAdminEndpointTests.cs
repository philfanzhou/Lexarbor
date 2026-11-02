using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class EnrichmentAdminEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Paths = ["/admin/enrichment/tasks", "/admin/enrichment/summary", "/admin/enrichment/tasks/unknown"];
    [Theory]
    [InlineData(null, 401)]
    [InlineData("student", 403)]
    [InlineData("submission", 403)]
    public async Task AdminEndpoints_DenyAnonymousAndOrdinaryTokensBeforeQueryOrRetry(string? role, int status)
    {
        using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        if (role != null) client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(role));
        foreach (var path in Paths) Assert.Equal((HttpStatusCode)status, (await client.GetAsync(path, Ct)).StatusCode);
        Assert.Equal((HttpStatusCode)status, (await client.PostAsJsonAsync("/admin/enrichment/tasks/unknown/retry",
            new { expectedRound = 1, retryKey = Guid.NewGuid().ToString() }, Ct)).StatusCode);
    }
    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "curator")]
    [InlineData(true, "curator")]
    public async Task AdminEndpoints_UseCookieCsrfBearerPriorityAndCustomAdminRole(bool cookie, string role)
    {
        using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = factory.CreateClient();
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie(role));
        else client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(role));
        var id = await Failed(factory);
        foreach (var path in Paths.Take(2)) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Paths[2], Ct)).StatusCode);
        var key = Guid.NewGuid().ToString(); var url = "/admin/enrichment/tasks/" + id + "/retry";
        var body = new { expectedRound = 1, retryKey = key };
        if (cookie)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, body, Ct)).StatusCode);
            AdminTestAntiforgery.Attach(client, factory);
        }
        var accepted = await Data(await client.PostAsJsonAsync(url, body, Ct));
        Assert.Equal(2, accepted.GetProperty("round").GetInt32()); Assert.False(accepted.GetProperty("replayed").GetBoolean());
        if (cookie)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-synthetic-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Paths[0], Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, body, Ct)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(role));
            client.DefaultRequestHeaders.Remove(AdminTestAntiforgery.HeaderName);
        }
        Assert.True((await Data(await client.PostAsJsonAsync(url, body, Ct))).GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new { expectedRound = 1, retryKey = Guid.NewGuid().ToString() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/admin/enrichment/tasks/unknown/retry", body, Ct)).StatusCode);
        var detail = await Data(await client.GetAsync("/admin/enrichment/tasks/" + id + "?historySize=1", Ct));
        Assert.Equal(2, detail.GetProperty("rounds").GetProperty("totalCount").GetInt32());
        Assert.Single(detail.GetProperty("rounds").GetProperty("items").EnumerateArray());
        var json = detail.GetRawText();
        foreach (var forbidden in new[] { "planJson", "configurationVersion", "lease", "retryKey", "uploadIdempotencyKey", "bucket", "objectKey", "synthetic-private-value" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("/admin/enrichment/tasks?page=0")]
    [InlineData("/admin/enrichment/tasks?size=101")]
    [InlineData("/admin/enrichment/tasks?state=Invalid")]
    [InlineData("/admin/enrichment/summary?locale=uk")]
    [InlineData("/admin/enrichment/summary?step=Invalid")]
    [InlineData("/admin/enrichment/tasks/unknown?historySize=101")]
    [InlineData("/admin/enrichment/tasks/unknown?historyPage=0")]
    public async Task AdminEndpoints_RejectInvalidFiltersAndBounds(string path)
    {
        using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path, Ct)).StatusCode);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expectedRound\":0,\"retryKey\":\"bad\"}")]
    [InlineData("{\"expectedRound\":\"1\",\"retryKey\":\"bad\"}")]
    [InlineData("{\"expectedRound\":1,\"expectedRound\":2}")]
    [InlineData("{\"batch\":true}")]
    public async Task AdminEndpoints_RejectInvalidRetryShapeWithoutWrites(string json)
    {
        using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/enrichment/tasks/unknown/retry", new StringContent(json, Encoding.UTF8, "application/json"), Ct)).StatusCode);
    }
    [Fact]
    public async Task AdminGet_RealHttpIsReadOnlyAndFiltersMatchStablePages()
    {
        using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin"));
        var id = await Failed(factory);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var tasks = scope.ServiceProvider.GetRequiredService<IEnrichmentTaskRepository>();
        await tasks.EnsureAsync(new("admin-word", null, AudioLocale.EnUs, EnrichmentStep.Audio), "synthetic-private-value",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ct);
        var before = await EnrichmentTargetExecutorTests.Dump(db);
        var page = await Data(await client.GetAsync("/admin/enrichment/tasks?size=1", Ct));
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal("en-GB", page.GetProperty("items")[0].GetProperty("locale").GetString());
        var next = await Data(await client.GetAsync("/admin/enrichment/tasks?size=1&page=2", Ct));
        Assert.Equal("en-US", next.GetProperty("items")[0].GetProperty("locale").GetString());
        var filtered = await Data(await client.GetAsync("/admin/enrichment/tasks?wordId=admin-word&locale=en-GB&step=Audio&state=Failed", Ct));
        Assert.Equal(1, filtered.GetProperty("totalCount").GetInt32());
        await Data(await client.GetAsync("/admin/enrichment/summary", Ct));
        await Data(await client.GetAsync("/admin/enrichment/tasks/" + id, Ct));
        Assert.Equal(before, await EnrichmentTargetExecutorTests.Dump(db));
    }

    private static async Task<string> Failed(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.Vocabularies.Add(new() { Id = "admin-word", Word = "example" }); await db.SaveChangesAsync(Ct);
        var tasks = scope.ServiceProvider.GetRequiredService<IEnrichmentTaskRepository>();
        var task = (await tasks.EnsureAsync(new("admin-word", null, AudioLocale.EnGb, EnrichmentStep.Audio), "synthetic-private-value", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ct))!;
        var guard = (await tasks.ClaimAsync(task.Id, "synthetic-private-value", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), Ct))!;
        await tasks.SavePlanAsync(guard, new("synthetic-private-value", null, null, []), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ct);
        await tasks.StopAsync(guard, "synthetic_private_value", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ct);
        return task.Id;
    }
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)); return doc.RootElement.GetProperty("data").Clone();
    }
}
