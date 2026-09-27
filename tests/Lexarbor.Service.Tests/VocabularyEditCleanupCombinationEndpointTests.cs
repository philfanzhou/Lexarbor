using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Endpoint-level combination of the W/M replacement PUTs and the C cleanup
/// commands on one real pipeline instance (routing, VocabularyAdmin
/// authorization, unified failure envelope, and the shared real SQLite
/// connection), per the parent model in #96's end-to-end scenario 6: editing
/// first means the cleanup deletes the current row; cleaning up first makes the
/// later edit 404 without revival or partial writes.
/// </summary>
public class VocabularyEditCleanupCombinationEndpointTests
{
    [Fact]
    public async Task MeaningPutThenPreviewAndCommit_RemovesEditedRow_AndLaterPutIs404()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory);

        // M first: the replacement lands on the shared word's first meaning.
        using (var put = await client.PutAsync("/admin/vocabulary-books/A/words/shared/meanings/a1",
            MeaningBody("v.", "edited-meaning", "edited example"), TestContext.Current.CancellationToken))
        {
            await SuccessAsync(put);
        }
        var edited = await MeaningAsync(factory.Services, "a1");
        Assert.Equal("v.", edited.PartOfSpeech);
        Assert.Equal("edited-meaning", edited.Meaning);
        Assert.Equal("edited example", edited.Example);

        // The preview and the commit both resolve R against the edited row.
        using (var preview = await PostCleanupAsync(client, """{"action":"removeMeaning","wordId":"shared","meaningId":"a1"}""", preview: true))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var data = await DataAsync(preview);
            Assert.Equal((1, 1, 0), (Get(data, "affectedWordCount"), Get(data, "meaningCount"), Get(data, "orphanWordCount")));
        }
        using (var commit = await PostCleanupAsync(client, """{"action":"removeMeaning","wordId":"shared","meaningId":"a1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
            using var data = await DataAsync(commit);
            Assert.Equal((1, 1, 0, false), (Get(data, "affectedWordCount"), Get(data, "deletedMeaningCount"), Get(data, "deletedWordCount"), data.RootElement.GetProperty("deletedBook").GetBoolean()));
        }

        Assert.Null(await MeaningOrDefaultAsync(factory.Services, "a1"));
        Assert.Equal(((string?)null, "a2", (string?)null), AsTuple(await MeaningAsync(factory.Services, "a2")));
        Assert.Equal(((string?)null, "b1", (string?)null), AsTuple(await MeaningAsync(factory.Services, "b1")));
        Assert.True(await WordExistsAsync(factory.Services, "shared"));
        Assert.True(await WordExistsAsync(factory.Services, "historical"));

        // The removed id is not revived by a later replacement.
        using (var putAgain = await client.PutAsync("/admin/vocabulary-books/A/words/shared/meanings/a1",
            MeaningBody(null, "revived", null), TestContext.Current.CancellationToken))
        {
            await FailureAsync(putAgain, HttpStatusCode.NotFound);
        }
        Assert.Null(await MeaningOrDefaultAsync(factory.Services, "a1"));
    }

    [Fact]
    public async Task CleanupThenMeaningPut_Is404OnRemovedTargets_WithoutWriting()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory);

        using (var commit = await PostCleanupAsync(client, """{"action":"clear"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        }
        var after = await StateAsync(factory.Services);

        using (var meaningPut = await client.PutAsync("/admin/vocabulary-books/A/words/shared/meanings/a1",
            MeaningBody(null, "revived", null), TestContext.Current.CancellationToken))
        {
            await FailureAsync(meaningPut, HttpStatusCode.NotFound);
        }
        using (var wordPut = await client.PutAsync("/admin/vocabulary/only-a",
            WordBody("revived", null, null), TestContext.Current.CancellationToken))
        {
            await FailureAsync(wordPut, HttpStatusCode.NotFound);
        }
        Assert.Equal(after, await StateAsync(factory.Services));
    }

    [Fact]
    public async Task WordPutThenClearCleanup_KeepsDisabledReferences_AndLaterPutIs404()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory);

        // W first: the shared fields of A's exclusive word are replaced.
        using (var put = await client.PutAsync("/admin/vocabulary/only-a",
            WordBody(" Renamed ", "new-uk", " "), TestContext.Current.CancellationToken))
        {
            await SuccessAsync(put);
        }

        using (var preview = await PostCleanupAsync(client, """{"action":"clear"}""", preview: true))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var data = await DataAsync(preview);
            Assert.Equal((2, 3, 1), (Get(data, "affectedWordCount"), Get(data, "meaningCount"), Get(data, "orphanWordCount")));
        }
        using (var commit = await PostCleanupAsync(client, """{"action":"clear"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
            using var data = await DataAsync(commit);
            Assert.False(data.RootElement.GetProperty("deletedBook").GetBoolean());
        }

        // shared survives through disabled B with the edited fields; the
        // exclusive word lost its last reference and is gone, so a later edit 404s.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var shared = await db.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "shared", TestContext.Current.CancellationToken);
            Assert.Equal("shared", shared.Word);
            Assert.False(await db.Vocabularies.AsNoTracking().AnyAsync(v => v.Id == "only-a", TestContext.Current.CancellationToken));
            Assert.True(await db.Vocabularies.AsNoTracking().AnyAsync(v => v.Id == "historical", TestContext.Current.CancellationToken));
            Assert.True(await db.VocabularyBooks.AsNoTracking().AnyAsync(b => b.Id == "A", TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.VocabularyMeanings.AsNoTracking().CountAsync(m => m.BookId == "B", TestContext.Current.CancellationToken));
        }
        using (var putAgain = await client.PutAsync("/admin/vocabulary/only-a",
            WordBody("revived", null, null), TestContext.Current.CancellationToken))
        {
            await FailureAsync(putAgain, HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task ClearCleanupThenWordPut_SharedWordStaysEditable()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        Authenticate(client, factory);

        using (var commit = await PostCleanupAsync(client, """{"action":"clear"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        }

        using (var put = await client.PutAsync("/admin/vocabulary/shared",
            WordBody("still-shared", null, null), TestContext.Current.CancellationToken))
        {
            await SuccessAsync(put);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var shared = await db.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "shared", TestContext.Current.CancellationToken);
            Assert.Equal("still-shared", shared.Word);
            Assert.True(await db.VocabularyMeanings.AsNoTracking().AnyAsync(m => m.Id == "b1", TestContext.Current.CancellationToken));
        }
    }

    private static StringContent MeaningBody(string? partOfSpeech, string meaning, string? example) =>
        new(JsonSerializer.Serialize(new { partOfSpeech, meaning, example }), Encoding.UTF8, "application/json");
    private static StringContent WordBody(string word, string? phoneticUk, string? phoneticUs) =>
        new(JsonSerializer.Serialize(new { word, phoneticUk, phoneticUs }), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostCleanupAsync(HttpClient client, string body, bool preview = false) =>
        client.PostAsync($"/admin/vocabulary-books/A/cleanup{(preview ? "/preview" : "")}",
            new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static void Authenticate(HttpClient client, VocabularyWebApplicationFactory factory) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));

    private static async Task SuccessAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(json.RootElement.GetProperty("data").GetProperty("success").GetBoolean());
    }

    private static async Task<JsonDocument> DataAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return JsonDocument.Parse(body.RootElement.GetProperty("data").GetRawText());
    }

    private static int Get(JsonDocument data, string name) => data.RootElement.GetProperty(name).GetInt32();

    private static async Task FailureAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "message", "success" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
    }

    private static (string?, string, string?) AsTuple(VocabularyMeaningEntity meaning) =>
        (meaning.PartOfSpeech, meaning.Meaning, meaning.Example);

    private static async Task<VocabularyMeaningEntity> MeaningAsync(IServiceProvider services, string id) =>
        (await MeaningOrDefaultAsync(services, id))!;

    private static async Task<VocabularyMeaningEntity?> MeaningOrDefaultAsync(IServiceProvider services, string id)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().VocabularyMeanings.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == id, TestContext.Current.CancellationToken);
    }

    private static async Task<bool> WordExistsAsync(IServiceProvider services, string id)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Vocabularies.AsNoTracking()
            .AnyAsync(v => v.Id == id, TestContext.Current.CancellationToken);
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = true }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = false });
        db.Vocabularies.AddRange(new[] { "shared", "only-a", "historical" }.Select(v => new VocabularyEntity { Id = v, Word = v }));
        db.VocabularyMeanings.AddRange(new[] { ("a1", "shared", "A"), ("a2", "shared", "A"), ("a-only", "only-a", "A"), ("b1", "shared", "B") }
            .Select(m => new VocabularyMeaningEntity { Id = m.Item1, VocabularyId = m.Item2, BookId = m.Item3, Meaning = m.Item1 }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> StateAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        return JsonSerializer.Serialize(new
        {
            Books = await db.VocabularyBooks.AsNoTracking().OrderBy(b => b.Id).ToListAsync(TestContext.Current.CancellationToken),
            Words = await db.Vocabularies.AsNoTracking().OrderBy(v => v.Id).ToListAsync(TestContext.Current.CancellationToken),
            Meanings = await db.VocabularyMeanings.AsNoTracking().OrderBy(m => m.Id).ToListAsync(TestContext.Current.CancellationToken)
        });
    }
}
