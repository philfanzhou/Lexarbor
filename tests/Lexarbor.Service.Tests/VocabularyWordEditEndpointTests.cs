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

public class VocabularyWordEditEndpointTests
{
    private const string Body = """{"word":" Replaced ","phoneticUk":null,"phoneticUs":" "}""";

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
        Assert.Equal("replaced||", await StateAsync(factory.Services));
        using var repeat = await PutAsync(client, """{"word":"replaced","phoneticUk":null,"phoneticUs":null}""");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal("replaced||", await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"word\":\"x\",\"phoneticUk\":null}")]
    [InlineData("{\"word\":\"x\",\"phoneticUs\":null}")]
    [InlineData("{\"phoneticUk\":null,\"phoneticUs\":null}")]
    [InlineData("{\"word\":null,\"phoneticUk\":null,\"phoneticUs\":null}")]
    [InlineData("{\"word\":\" \",\"phoneticUk\":null,\"phoneticUs\":null}")]
    [InlineData("{\"word\":1,\"phoneticUk\":null,\"phoneticUs\":null}")]
    [InlineData("{\"word\":\"x\",\"phoneticUk\":{},\"phoneticUs\":null}")]
    [InlineData("{\"word\":\"x\",\"phoneticUk\":null,\"phoneticUs\":false}")]
    [InlineData("{\"word\":\"x\",\"phoneticUk\":null,\"phoneticUs\":null,\"id\":\"other\"}")]
    [InlineData("{\"word\":\"x\",\"phoneticUk\":null,\"phoneticUs\":null,\"meanings\":[]}")]
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
        Assert.Equal("original|uk|us", await StateAsync(factory.Services));
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
        Assert.Equal("original|uk|us", await StateAsync(factory.Services));
    }

    [Fact]
    public async Task MissingAndConflictingWords_Return404And409WithoutPartialUpdate()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        using var missing = await client.PutAsync("/admin/vocabulary/missing", new StringContent(Body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        await FailureAsync(missing, HttpStatusCode.NotFound);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.Add(new VocabularyEntity { Id = "other", Word = "REPLACED" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var conflict = await PutAsync(client, Body);
        await FailureAsync(conflict, HttpStatusCode.Conflict);
        Assert.Equal("original|uk|us", await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData("Ä", "ä")]
    [InlineData("\u2003ORIGINAL\u00a0", "original")]
    public async Task HistoricalUnicodeDuplicate_Returns409WithoutPartialUpdate(string historical, string requested)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.Add(new VocabularyEntity { Id = "other", Word = historical });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var response = await PutAsync(client, JsonSerializer.Serialize(new { word = requested, phoneticUk = (string?)null, phoneticUs = "changed" }));
        await FailureAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("original|uk|us", await StateAsync(factory.Services));
    }

    [Fact]
    public async Task ExternalSqliteWriter_Returns503WithRetryAfter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexarbor-word-http-{Guid.NewGuid():N}.db");
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
            Assert.Equal("original|uk|us", await StateAsync(fileFactory.Services));
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private static void Authenticate(HttpClient client, VocabularyWebApplicationFactory factory, bool cookie, string role)
    {
        var token = factory.CreateToken(role);
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", $"{VocabularyWebApplicationFactory.CookieName}={token}");
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string body) => client.PutAsync("/admin/vocabulary/w",
        new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
    private static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.Vocabularies.Add(new VocabularyEntity { Id = "w", Word = "original", PhoneticUk = "uk", PhoneticUs = "us" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    private static async Task<string> StateAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var word = await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Vocabularies.AsNoTracking()
            .SingleAsync(v => v.Id == "w", TestContext.Current.CancellationToken);
        return $"{word.Word}|{word.PhoneticUk}|{word.PhoneticUs}";
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
