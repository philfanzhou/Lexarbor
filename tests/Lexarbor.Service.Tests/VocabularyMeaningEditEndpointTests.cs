using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public class VocabularyMeaningEditEndpointTests
{
    private const string Body = """{"meaning":" Replaced ","partOfSpeech":null,"example":" "}""";

    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "maintainer")]
    [InlineData(true, "maintainer")]
    public async Task CompleteReplacement_CookieBearerAndCustomRole(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, cookie, role);
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var response = await PutAsync(client, Body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(json.RootElement.GetProperty("data").GetProperty("success").GetBoolean());
        Assert.Equal("Replaced||", await StateAsync(factory.Services));
        using var repeat = await PutAsync(client, """{"meaning":"Replaced","partOfSpeech":null,"example":null}""");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal("Replaced||", await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"meaning\":\"x\",\"partOfSpeech\":null}")]
    [InlineData("{\"meaning\":\"x\",\"example\":null}")]
    [InlineData("{\"partOfSpeech\":null,\"example\":null}")]
    [InlineData("{\"meaning\":null,\"partOfSpeech\":null,\"example\":null}")]
    [InlineData("{\"meaning\":\" \",\"partOfSpeech\":null,\"example\":null}")]
    [InlineData("{\"meaning\":1,\"partOfSpeech\":null,\"example\":null}")]
    [InlineData("{\"meaning\":\"x\",\"partOfSpeech\":{},\"example\":null}")]
    [InlineData("{\"meaning\":\"x\",\"partOfSpeech\":null,\"example\":false}")]
    [InlineData("{\"meaning\":\"x\",\"partOfSpeech\":null,\"example\":null,\"id\":\"other\"}")]
    [InlineData("{\"meaning\":\"x\",\"partOfSpeech\":null,\"example\":null,\"meanings\":[]}")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task InvalidShape_Returns400WithoutWrites(string body)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        using var response = await PutAsync(client, body);
        await FailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("original|n.|example", await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie-no-csrf", 403)]
    public async Task UnauthorizedWrite_ChangesNothing(string identity, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        if (identity != "anonymous") Authenticate(client, factory, identity == "cookie-no-csrf", identity == "student" ? "student" : "admin");
        using var response = await PutAsync(client, Body);
        await FailureAsync(response, (HttpStatusCode)status);
        Assert.Equal("original|n.|example", await StateAsync(factory.Services));
    }

    [Fact]
    public async Task MissingAndConflictingMeanings_Return404And409WithoutPartialUpdate()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        using var missing = await client.PutAsync("/admin/vocabulary-books/A/words/w/meanings/missing", new StringContent(Body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        await FailureAsync(missing, HttpStatusCode.NotFound);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "other-meaning", VocabularyId = "w", BookId = "A", PartOfSpeech = "", Meaning = "Replaced" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var conflict = await PutAsync(client, Body);
        await FailureAsync(conflict, HttpStatusCode.Conflict);
        Assert.Equal("original|n.|example", await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData("missing", "w", "a", 404)]
    [InlineData("A", "missing", "a", 404)]
    [InlineData("A", "w", "missing", 404)]
    [InlineData("B", "w", "a", 409)]
    [InlineData("A", "other", "a", 409)]
    public async Task PathOwnershipIsImmutable(string book, string word, string meaning, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        using var response = await client.PutAsync($"/admin/vocabulary-books/{book}/words/{word}/meanings/{meaning}",
            new StringContent(Body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        await FailureAsync(response, (HttpStatusCode)status);
        Assert.Equal("original|n.|example", await StateAsync(factory.Services));
    }

    [Fact]
    public async Task ExternalSqliteWriter_Returns503WithRetryAfter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexarbor-meaning-http-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = new VocabularyWebApplicationFactory();
            await using var fileFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<VocabularyDbContext>>();
                services.RemoveAll<VocabularyDbContext>();
                services.AddDbContext<VocabularyDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False;Default Timeout=1"));
            }));
            using var client = fileFactory.CreateClient();
            await SeedAsync(fileFactory.Services);
            Authenticate(client, factory, false, "admin");
            using var scope = fileFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                using var response = await PutAsync(client, Body);
                await FailureAsync(response, HttpStatusCode.ServiceUnavailable);
                Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
            }
            Assert.Equal("original|n.|example", await StateAsync(fileFactory.Services));
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private static void Authenticate(HttpClient client, VocabularyWebApplicationFactory factory, bool cookie, string role)
    {
        var token = factory.CreateToken(role);
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", $"{VocabularyWebApplicationFactory.CookieName}={token}");
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string body) => client.PutAsync("/admin/vocabulary-books/A/words/w/meanings/a",
        new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
    private static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = false }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = true });
        db.Vocabularies.AddRange(new VocabularyEntity { Id = "w", Word = "shared", PhoneticUk = "uk", PhoneticUs = "us" }, new VocabularyEntity { Id = "other", Word = "other" });
        db.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "a", VocabularyId = "w", BookId = "A", PartOfSpeech = "n.", Meaning = "original", Example = "example" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    private static async Task<string> StateAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var meaning = await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().VocabularyMeanings.AsNoTracking()
            .SingleAsync(m => m.Id == "a", TestContext.Current.CancellationToken);
        return $"{meaning.Meaning}|{meaning.PartOfSpeech}|{meaning.Example}";
    }
    private static async Task FailureAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "message", "success" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("original", body.RootElement.GetProperty("message").GetString());
    }
}
