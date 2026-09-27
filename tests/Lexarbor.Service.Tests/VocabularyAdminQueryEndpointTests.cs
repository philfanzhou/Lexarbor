using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public class VocabularyAdminQueryEndpointTests
{
    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "maintainer")]
    [InlineData(true, "maintainer")]
    public async Task AllThreeReads_UseRealAuthorizationAndExactManagementContracts(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = factory.CreateClient();
        var token = factory.CreateToken(role);
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", $"{VocabularyWebApplicationFactory.CookieName}={token}");
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBooks.Add(new VocabularyBookEntity { Id = "disabled", BookName = "Disabled", Status = false });
            db.Vocabularies.AddRange(new VocabularyEntity { Id = "w", Word = "word" }, new VocabularyEntity { Id = "h", Word = "historical" });
            db.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "m", VocabularyId = "w", BookId = "disabled", Meaning = "definition" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var all = await GetAsync(client, "/admin/vocabulary");
        Assert.Equal(2, all.RootElement.GetProperty("totalCount").GetInt32());
        var items = all.RootElement.GetProperty("items");
        Assert.Equal(new[] { "books", "id", "phoneticUk", "phoneticUs", "word" }, items[0].EnumerateObject().Select(p => p.Name).Order());
        Assert.Empty(items[0].GetProperty("books").EnumerateArray());
        using var detail = await GetAsync(client, "/admin/vocabulary/w");
        Assert.False(detail.RootElement.GetProperty("books")[0].GetProperty("status").GetBoolean());
        var meaning = detail.RootElement.GetProperty("meanings")[0];
        Assert.Equal(new[] { "bookId", "example", "id", "meaning", "partOfSpeech", "vocabularyId" }, meaning.EnumerateObject().Select(p => p.Name).Order());
        using var content = await GetAsync(client, "/admin/vocabulary-books/disabled/content");
        Assert.Equal(new[] { "book", "items", "meaningCount", "totalCount", "totalPage", "wordCount" }, content.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(1, content.RootElement.GetProperty("meaningCount").GetInt32());
        using var publicResult = await GetAsync(client, "/api/vocabulary?keyword=word");
        Assert.Empty(publicResult.RootElement.GetProperty("items").EnumerateArray());
        using var publicDetail = await client.GetAsync("/api/vocabulary/w?bookId=disabled", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publicDetail.StatusCode);
        using var oldWords = await GetAsync(client, "/admin/vocabulary-books/disabled/words");
        Assert.False(oldWords.RootElement.GetProperty("items")[0].TryGetProperty("books", out _));
    }

    [Theory]
    [InlineData("/admin/vocabulary")]
    [InlineData("/admin/vocabulary/w")]
    [InlineData("/admin/vocabulary-books/A/content")]
    public async Task AnonymousAndNonAdmin_ReadsAreRejectedBeforeResourceAccess(string path)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        foreach (var role in new[] { "", "student" })
        {
            if (role.Length > 0) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(role));
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(role.Length == 0 ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new[] { "message", "success" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        }
    }

    [Theory]
    [InlineData("/admin/vocabulary?page=-1", 400)]
    [InlineData("/admin/vocabulary?size=101", 400)]
    [InlineData("/admin/vocabulary?size=-1", 400)]
    [InlineData("/admin/vocabulary?page=2147483648", 400)]
    [InlineData("/admin/vocabulary?page=2147483647&size=100", 400)]
    [InlineData("/admin/vocabulary?bookId=missing", 404)]
    [InlineData("/admin/vocabulary/missing", 404)]
    [InlineData("/admin/vocabulary-books/missing/content", 404)]
    [InlineData("/admin/vocabulary-books/missing/content?size=101", 400)]
    [InlineData("/admin/vocabulary?size=100", 200)]
    [InlineData("/admin/vocabulary?page=0&size=0", 200)]
    public async Task ValidationAndMissingResources_KeepFailureEnvelope(string path, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(status == 200, body.RootElement.GetProperty("success").GetBoolean());
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return JsonDocument.Parse(body.RootElement.GetProperty("data").GetRawText());
    }
}
