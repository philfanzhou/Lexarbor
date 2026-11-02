using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Host;
using Lexarbor.Host.Configuration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class MeaningPhoneticAdminEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Route = "/admin/vocabulary-books/book/words/word/meanings/meaning";

    [Fact]
    public async Task ReadSetRepeatAndClear_KeepDefaultsAndOtherAccent_AndUseActualRuntimeVersions()
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        await Seed(factory); Auth(client, factory);
        var initial = await Read(client);
        Assert.Equal("A", initial.GetProperty("phoneticUk").GetString());
        Assert.Equal("meaning", initial.GetProperty("pronunciation").GetProperty("uk").GetProperty("source").GetString());
        Assert.Equal("default", initial.GetProperty("pronunciation").GetProperty("us").GetProperty("source").GetString());
        Assert.Equal("default-us", initial.GetProperty("pronunciation").GetProperty("us").GetProperty("phonetic").GetString());
        Assert.Equal(0, initial.GetProperty("expectedUk").GetProperty("epoch").GetInt64());
        using (var old = await client.PutAsJsonAsync(Route, Text("legacy"), Token)) Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        var afterLegacy = await Read(client); Assert.Equal("A", afterLegacy.GetProperty("phoneticUk").GetString());
        var set = Text("changed"); Set(set, afterLegacy, "Uk", "B"); Set(set, afterLegacy, "Us", "US");
        using (var response = await client.PutAsJsonAsync(Route, set, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await Read(client); Assert.Equal("B", saved.GetProperty("phoneticUk").GetString());
        Assert.Equal("US", saved.GetProperty("phoneticUs").GetString()); Assert.Equal("changed", saved.GetProperty("meaning").GetString());
        using (var old = await client.PutAsJsonAsync(Route, Text("changed"), Token)) Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal(saved.GetRawText(), (await Read(client)).GetRawText());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var runtime = scope.ServiceProvider.GetRequiredService<EnrichmentRuntimeConfiguration>().Current;
            foreach (var target in await db.EnrichmentTargets.ToListAsync(Token))
            {
                var round = await db.EnrichmentRounds.SingleAsync(row => row.TargetId == target.Id && row.Number == target.CurrentRound, Token);
                Assert.Equal(EnrichmentTargetExecutor.ConfigurationVersion(new(target.WordId, target.MeaningId, target.Locale, target.Step), runtime), round.ConfigurationVersion);
            }
            Assert.Equal(4, await db.EnrichmentTargets.CountAsync(Token));
            var word = await db.Vocabularies.SingleAsync(row => row.Id == "word", Token); Assert.Equal("default-uk", word.PhoneticUk); Assert.Equal("default-us", word.PhoneticUs);
        }
        var repeat = Text("changed"); Set(repeat, saved, "Uk", " [ B ] ");
        using (var response = await client.PutAsJsonAsync(Route, repeat, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(saved.GetProperty("expectedUk").GetRawText(), (await Read(client)).GetProperty("expectedUk").GetRawText());
        var clear = Text("changed"); Set(clear, saved, "Uk", null);
        using (var response = await client.PutAsJsonAsync(Route, clear, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cleared = await Read(client); Assert.Equal(JsonValueKind.Null, cleared.GetProperty("phoneticUk").ValueKind);
        Assert.Equal("default", cleared.GetProperty("pronunciation").GetProperty("uk").GetProperty("source").GetString());
        Assert.Equal("US", cleared.GetProperty("phoneticUs").GetString());
        Assert.Equal(saved.GetProperty("expectedUs").GetRawText(), cleared.GetProperty("expectedUs").GetRawText());
        using var stale = await client.PutAsJsonAsync(Route, set, Token); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Theory]
    [InlineData("missing-condition")]
    [InlineData("null-condition")]
    [InlineData("unknown-condition-field")]
    [InlineData("missing-own-asset")]
    [InlineData("negative-epoch")]
    [InlineData("bad-revision")]
    [InlineData("value-object")]
    [InlineData("value-array")]
    [InlineData("value-number")]
    [InlineData("value-bool")]
    [InlineData("too-long")]
    [InlineData("control")]
    [InlineData("condition-without-value")]
    [InlineData("null-condition-without-value")]
    public async Task InvalidNewShapes_RejectBeforeAnyWrite(string scenario)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        await Seed(factory); Auth(client, factory); var before = await Read(client); var body = Text("changed"); Set(body, before, "Uk", "B");
        var condition = JsonSerializer.Deserialize<Dictionary<string, object?>>(before.GetProperty("expectedUk"))!;
        body["expectedUk"] = condition;
        switch (scenario)
        {
            case "missing-condition": body.Remove("expectedUk"); break;
            case "null-condition": body["expectedUk"] = null; break;
            case "unknown-condition-field": condition["secret"] = "synthetic"; break;
            case "missing-own-asset": condition.Remove("ownAssetId"); break;
            case "negative-epoch": condition["epoch"] = -1; break;
            case "bad-revision": condition["sourceRevision"] = "invalid"; break;
            case "value-object": body["phoneticUk"] = new { value = "B" }; break;
            case "value-array": body["phoneticUk"] = new[] { "B" }; break;
            case "value-number": body["phoneticUk"] = 1; break;
            case "value-bool": body["phoneticUk"] = false; break;
            case "too-long": body["phoneticUk"] = new string('x', 513); break;
            case "control": body["phoneticUk"] = "a\nb"; break;
            case "condition-without-value": body.Remove("phoneticUk"); break;
            case "null-condition-without-value": body.Remove("phoneticUk"); body["expectedUk"] = null; break;
        }
        using var response = await client.PutAsJsonAsync(Route, body, Token);
        await HttpFailureAssertions.AssertFailureAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(before.GetRawText(), (await Read(client)).GetRawText());
    }

    [Fact]
    public async Task ClearBothAccents_CancelsSpecificWorkAndRestoresDefaultWithoutNewValues()
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        await Seed(factory); Auth(client, factory); var set = Text("original"); var initial = await Read(client);
        Set(set, initial, "Uk", "B"); Set(set, initial, "Us", "US");
        using (var response = await client.PutAsJsonAsync(Route, set, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await Read(client); var clear = Text("original"); Set(clear, saved, "Uk", " "); Set(clear, saved, "Us", null);
        using (var response = await client.PutAsJsonAsync(Route, clear, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read(client);
        foreach (var side in new[] { "uk", "us" })
            Assert.Equal("default", result.GetProperty("pronunciation").GetProperty(side).GetProperty("source").GetString());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        foreach (var target in await db.EnrichmentTargets.ToListAsync(Token))
            Assert.Equal(EnrichmentState.Cancelled, (await db.EnrichmentRounds.SingleAsync(row => row.TargetId == target.Id && row.Number == target.CurrentRound, Token)).State);
    }

    [Fact]
    public async Task SecondAccentConflictAndAba_RejectWholeTextAndBothSides()
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        await Seed(factory); Auth(client, factory); var before = await Read(client);
        var invalid = Text("changed"); Set(invalid, before, "Uk", "B"); Set(invalid, before, "Us", "US");
        invalid["expectedUs"] = new { epoch = 99, sourceRevision = before.GetProperty("expectedUs").GetProperty("sourceRevision").GetString(), ownAssetId = (string?)null };
        using (var response = await client.PutAsJsonAsync(Route, invalid, Token)) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before.GetRawText(), (await Read(client)).GetRawText());
        var old = Text("original"); Set(old, before, "Uk", "B");
        using (var response = await client.PutAsJsonAsync(Route, old, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var revert = Text("original"); Set(revert, await Read(client), "Uk", "A");
        using (var response = await client.PutAsJsonAsync(Route, revert, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stale = await client.PutAsJsonAsync(Route, old, Token); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2, (await Read(client)).GetProperty("expectedUk").GetProperty("epoch").GetInt64());
    }

    [Fact]
    public async Task PhrasePositions_AllowOmissionExistingAndClear_ButNoNewSpecific()
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        await Seed(factory); Auth(client, factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
            db.VocabularyMeaningUnits.Add(new() { UnitId = "unit", BookId = "book", MeaningId = "meaning", EntryKind = "phrase" });
            await db.SaveChangesAsync(Token);
        }
        var before = await Read(client); var body = Text("original"); Set(body, before, "Uk", "B");
        using (var response = await client.PutAsJsonAsync(Route, body, Token)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Set(body, before, "Uk", "[A]");
        using (var response = await client.PutAsJsonAsync(Route, body, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Set(body, await Read(client), "Uk", " ");
        using (var response = await client.PutAsJsonAsync(Route, body, Token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Read(client)).GetProperty("phoneticUk").ValueKind);
    }

    [Theory]
    [InlineData("other-book", "word", "meaning", 409)]
    [InlineData("book", "other-word", "meaning", 409)]
    [InlineData("book", "word", "missing", 404)]
    [InlineData("missing", "word", "meaning", 404)]
    [InlineData("book", "missing", "meaning", 404)]
    public async Task ProvidedSpecifics_KeepOwnershipChecks(string book, string word, string meaning, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory(); using var client = factory.CreateClient();
        await Seed(factory); Auth(client, factory); var before = await Read(client); var body = Text("changed"); Set(body, before, "Uk", "B");
        using var response = await client.PutAsJsonAsync($"/admin/vocabulary-books/{book}/words/{word}/meanings/{meaning}", body, Token);
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.Equal(before.GetRawText(), (await Read(client)).GetRawText());
    }

    private static Dictionary<string, object?> Text(string meaning) => new() { ["partOfSpeech"] = "n.", ["meaning"] = meaning, ["example"] = "example" };
    private static void Set(Dictionary<string, object?> body, JsonElement current, string side, string? value)
    { body["phonetic" + side] = value; body["expected" + side] = current.GetProperty("expected" + side); }
    private static void Auth(HttpClient client, VocabularyWebApplicationFactory factory) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
    private static async Task<JsonElement> Read(HttpClient client)
    {
        using var response = await client.GetAsync("/admin/vocabulary/word", Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return json.RootElement.GetProperty("data").GetProperty("meanings")[0].Clone();
    }
    private static async Task Seed(VocabularyWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.Add(new VocabularyBookEntity { Id = "book", BookName = "book", Status = false });
        db.VocabularyBooks.Add(new VocabularyBookEntity { Id = "other-book", BookName = "other", Status = true });
        db.Vocabularies.Add(new VocabularyEntity { Id = "word", Word = "word", PhoneticUk = "default-uk", PhoneticUs = "default-us" });
        db.Vocabularies.Add(new VocabularyEntity { Id = "other-word", Word = "other" });
        db.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "meaning", VocabularyId = "word", BookId = "book", PartOfSpeech = "n.", Meaning = "original", Example = "example", PhoneticUk = "A" });
        await db.SaveChangesAsync(Token);
    }
}
