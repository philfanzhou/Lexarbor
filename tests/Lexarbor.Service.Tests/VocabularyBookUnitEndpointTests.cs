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

public class VocabularyBookUnitEndpointTests
{
    private const string Body = """{"number":2,"title":" Unit 2 "}""";

    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "maintainer")]
    [InlineData(true, "maintainer")]
    public async Task UnitLifecycle_CookieBearerAndCustomRole(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services, seedUnit: false);
        Authenticate(client, factory, cookie, role);
        AdminTestAntiforgery.Attach(client, factory);

        // Create runs on a disabled book: a book's units are maintained the
        // same way whether it is enabled, and a padded title is trimmed to
        // display text.
        using var second = await PostAsync(client, Body);
        UnitDto unit2 = await SuccessAsync(second);
        Assert.Equal("A", unit2.BookId);
        Assert.Equal(2, unit2.Number);
        Assert.Equal("Unit 2", unit2.Title);
        Assert.Equal(0, unit2.MeaningCount);
        Assert.False(string.IsNullOrEmpty(unit2.Id));

        // A whitespace-only title normalizes to null like on create.
        using var first = await PostAsync(client, """{"number":1,"title":"  "}""");
        UnitDto unit1 = await SuccessAsync(first);
        Assert.Null(unit1.Title);

        using var list = await client.GetAsync("/admin/vocabulary-books/A/units", TestContext.Current.CancellationToken);
        UnitDto[] units = await ListAsync(list);
        // Ascending unit order with per-unit counts, from one grouped read.
        Assert.Equal(new[] { 1, 2 }, units.Select(unit => unit.Number));
        Assert.Equal(new[] { unit1.Id, unit2.Id }, units.Select(unit => unit.Id));
        Assert.All(units, unit => Assert.Equal(0, unit.MeaningCount));

        // A replace may renumber onto a free number and carries the title.
        using var renumbered = await client.PutAsync($"/admin/vocabulary-books/A/units/{unit2.Id}",
            Json("""{"number":3,"title":" Unit 3 "}"""), TestContext.Current.CancellationToken);
        UnitDto unit3 = await SuccessAsync(renumbered);
        Assert.Equal(3, unit3.Number);
        Assert.Equal("Unit 3", unit3.Title);
        Assert.Equal(0, unit3.MeaningCount);

        // Two meanings assigned to the unit are what its list count reports,
        // and keeping its own number is not a conflict.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyMeaningUnits.AddRange(
                new VocabularyMeaningUnitEntity { UnitId = unit3.Id, MeaningId = "a", BookId = "A" },
                new VocabularyMeaningUnitEntity { UnitId = unit3.Id, MeaningId = "a2", BookId = "A" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var kept = await client.PutAsync($"/admin/vocabulary-books/A/units/{unit3.Id}",
            Json("""{"number":3,"title":null}"""), TestContext.Current.CancellationToken);
        UnitDto replaced = await SuccessAsync(kept);
        Assert.Equal(3, replaced.Number);
        Assert.Null(replaced.Title);
        Assert.Equal(2, replaced.MeaningCount);

        using var counted = await client.GetAsync("/admin/vocabulary-books/A/units", TestContext.Current.CancellationToken);
        UnitDto[] countedUnits = await ListAsync(counted);
        Assert.Equal(new[] { 1, 3 }, countedUnits.Select(unit => unit.Number));
        Assert.Equal(new[] { 0, 2 }, countedUnits.Select(unit => unit.MeaningCount));

        // Deleting removes only the unit and its assignments.
        using var deleted = await client.DeleteAsync($"/admin/vocabulary-books/A/units/{unit3.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var deletedBody = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(deletedBody.RootElement.GetProperty("data").GetProperty("success").GetBoolean());
        using var remaining = await client.GetAsync("/admin/vocabulary-books/A/units", TestContext.Current.CancellationToken);
        UnitDto[] survivors = await ListAsync(remaining);
        Assert.Equal(new[] { unit1.Id }, survivors.Select(unit => unit.Id));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            // The meanings, the shared word, and every assignment of the other
            // unit survive; the deleted unit's assignments are gone.
            Assert.Equal(2, await db.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.VocabularyMeaningUnits.AsNoTracking()
                .CountAsync(membership => membership.UnitId == unit3.Id, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"number\":2}")]
    [InlineData("{\"title\":null}")]
    [InlineData("{\"title\":\"Unit 2\"}")]
    [InlineData("{\"number\":null,\"title\":null}")]
    [InlineData("{\"number\":0,\"title\":null}")]
    [InlineData("{\"number\":-3,\"title\":null}")]
    [InlineData("{\"number\":2.5,\"title\":null}")]
    [InlineData("{\"number\":[],\"title\":null}")]
    [InlineData("{\"number\":2,\"title\":5}")]
    [InlineData("{\"number\":2,\"title\":null,\"id\":\"other\"}")]
    [InlineData("{\"number\":2,\"title\":null,\"bookId\":\"B\"}")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task InvalidShape_Returns400WithoutWrites(string body)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        AdminTestAntiforgery.Attach(client, factory);
        using var response = await PostAsync(client, body);
        await FailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("A:2:", await StateAsync(factory.Services));
    }

    [Fact]
    public async Task MissingNumber_ReturnsItsOwnMessage()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        AdminTestAntiforgery.Attach(client, factory);
        using var response = await PostAsync(client, "{}");
        await FailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("A:2:", await StateAsync(factory.Services));

        using var put = await client.PutAsync("/admin/vocabulary-books/A/units/u1",
            Json("""{"title":null}"""), TestContext.Current.CancellationToken);
        await FailureAsync(put, HttpStatusCode.BadRequest);
        Assert.Equal("A:2:", await StateAsync(factory.Services));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("student", 403)]
    [InlineData("cookie-no-csrf", 401)]
    public async Task UnauthorizedWrite_ChangesNothing(string identity, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        if (identity != "anonymous") Authenticate(client, factory, identity == "cookie-no-csrf", identity == "student" ? "student" : "admin");
        using var response = await PostAsync(client, Body);
        await FailureAsync(response, (HttpStatusCode)status);
        Assert.Equal("A:2:", await StateAsync(factory.Services));
    }

    [Fact]
    public async Task DuplicateNumber_PostAndRenumber_Return409WithoutWrites()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        AdminTestAntiforgery.Attach(client, factory);

        // The seeded unit already holds number 2 in book A.
        using var duplicate = await PostAsync(client, Body);
        await FailureAsync(duplicate, HttpStatusCode.Conflict);
        Assert.Equal("A:2:", await StateAsync(factory.Services));

        using var free = await PostAsync(client, """{"number":6,"title":null}""");
        UnitDto unit6 = await SuccessAsync(free);

        // Renumbering the seeded unit onto 6 conflicts; its own row is unchanged.
        using var renumber = await client.PutAsync("/admin/vocabulary-books/A/units/u1",
            Json("""{"number":6,"title":null}"""), TestContext.Current.CancellationToken);
        await FailureAsync(renumber, HttpStatusCode.Conflict);
        Assert.Equal("A:2:,A:6:", await StateAsync(factory.Services));
        Assert.NotNull(unit6);
    }

    [Theory]
    [InlineData("missing", "u1", "PUT", 404)]
    [InlineData("A", "missing", "PUT", 404)]
    [InlineData("B", "u1", "PUT", 409)]
    [InlineData("missing", "u1", "DELETE", 404)]
    [InlineData("A", "missing", "DELETE", 404)]
    [InlineData("B", "u1", "DELETE", 409)]
    public async Task PathOwnershipIsImmutable(string book, string unit, string method, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        AdminTestAntiforgery.Attach(client, factory);
        using var response = method == "PUT"
            ? await client.PutAsync($"/admin/vocabulary-books/{book}/units/{unit}",
                Json(Body), TestContext.Current.CancellationToken)
            : await client.DeleteAsync($"/admin/vocabulary-books/{book}/units/{unit}", TestContext.Current.CancellationToken);
        await FailureAsync(response, (HttpStatusCode)status);
        // The seeded unit of the disabled book A survived every refusal,
        // untouched.
        Assert.Equal("A:2:", await StateAsync(factory.Services));
    }

    [Fact]
    public async Task Listing_ExistingBookWithoutUnits_ReturnsEmptyList()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory, false, "admin");
        using var missing = await client.GetAsync("/admin/vocabulary-books/missing/units", TestContext.Current.CancellationToken);
        await FailureAsync(missing, HttpStatusCode.NotFound);

        using var response = await client.GetAsync("/admin/vocabulary-books/B/units", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UnitDto[] units = await ListAsync(response);
        Assert.Empty(units);
    }

    [Fact]
    public async Task ExternalSqliteWriter_Returns503WithRetryAfter()
    {
        var path = Path.Combine(
            VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-units-http-{Guid.NewGuid():N}"),
            "vocabulary.db");
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
            AdminTestAntiforgery.Attach(client, factory);
            using var scope = fileFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                using var response = await PostAsync(client, Body);
                await FailureAsync(response, HttpStatusCode.ServiceUnavailable);
                Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
            }
            Assert.Equal("A:2:", await StateAsync(fileFactory.Services));
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private sealed record UnitDto(string Id, string BookId, int Number, string? Title, int MeaningCount);

    private static void Authenticate(HttpClient client, VocabularyWebApplicationFactory factory, bool cookie, string role)
    {
        // The retired JWT cookie authenticates nothing; the cookie variant now uses a
        // real opaque session cookie.
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", factory.CreateSessionCookie(role));
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken(role));
    }
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) => client.PostAsync("/admin/vocabulary-books/A/units",
        Json(body), TestContext.Current.CancellationToken);
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task SeedAsync(IServiceProvider services, bool seedUnit = true)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = false }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = true });
        db.Vocabularies.Add(new VocabularyEntity { Id = "w", Word = "shared", PhoneticUk = "uk", PhoneticUs = "us" });
        db.VocabularyMeanings.AddRange(
            new VocabularyMeaningEntity { Id = "a", VocabularyId = "w", BookId = "A", PartOfSpeech = "n.", Meaning = "first" },
            new VocabularyMeaningEntity { Id = "a2", VocabularyId = "w", BookId = "A", PartOfSpeech = "n.", Meaning = "second" });
        if (seedUnit)
        {
            db.VocabularyBookUnits.Add(new VocabularyBookUnitEntity { Id = "u1", BookId = "A", Number = 2, Title = null });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    private static async Task<string> StateAsync(IServiceProvider services)
    {
        // "<bookId>:<number>:<title>" rows in book-then-number order: enough
        // to see any write a refused request must not have made.
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var units = await db.VocabularyBookUnits.AsNoTracking()
            .OrderBy(unit => unit.BookId).ThenBy(unit => unit.Number)
            .ToListAsync(TestContext.Current.CancellationToken);
        return string.Join(",", units.Select(unit => $"{unit.BookId}:{unit.Number}:{unit.Title ?? string.Empty}"));
    }
    private static async Task<UnitDto> SuccessAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var data = body.RootElement.GetProperty("data");
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        return new UnitDto(
            data.GetProperty("id").GetString()!,
            data.GetProperty("bookId").GetString()!,
            data.GetProperty("number").GetInt32(),
            data.TryGetProperty("title", out var title) ? title.GetString() : null,
            data.GetProperty("meaningCount").GetInt32());
    }
    private static async Task<UnitDto[]> ListAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. body.RootElement.GetProperty("data").GetProperty("units").EnumerateArray().Select(unit => new UnitDto(
            unit.GetProperty("id").GetString()!,
            unit.GetProperty("bookId").GetString()!,
            unit.GetProperty("number").GetInt32(),
            unit.GetProperty("title").GetString(),
            unit.GetProperty("meaningCount").GetInt32()))];
    }
    private static Task FailureAsync(HttpResponseMessage response, HttpStatusCode status)
        => TestInfrastructure.HttpFailureAssertions.AssertFailureAsync(response, status);
}
