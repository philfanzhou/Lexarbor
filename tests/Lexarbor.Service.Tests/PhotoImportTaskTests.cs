using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Host;
using Lexarbor.Learning.Database;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Lexarbor.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class PhotoImportTaskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Photo = [0xff, 0xd8, 0xff, 1];

    [Fact]
    public async Task StudentRetriesTheStoredPhotoAndFinishesEachEntryOnce()
    {
        var vision = new Mock<IVocabularyPhotoRecognizer>(MockBehavior.Strict);
        vision.SetupSequence(x => x.RecognizeAsync(It.Is<VocabPhoto>(p => p.Content.SequenceEqual(Photo)), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VocabPhotoImportUnavailableException())
            .ReturnsAsync([new("apple", null, "fruit"), new("pear", null, "fruit", "Check handwriting")])
            .ReturnsAsync([]);
        var meanings = new[] { "apple", "pear" }.ToDictionary(word => word,
            word => (IReadOnlyList<VocabCatalogMeaning>)[new(word, word, word, null, null, "book", "Book", word, "n.", "fruit", "fruit")]);
        var lookup = new Mock<IVocabularyPhotoCatalog>();
        lookup.Setup(x => x.LookupMeaningsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(meanings);
        var catalog = new Mock<IVocabularyNotebookCatalog>(MockBehavior.Strict);
        foreach (var (word, matches) in meanings)
            catalog.Setup(x => x.SearchAsync(word, It.IsAny<CancellationToken>())).ReturnsAsync(matches);

        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Run each real worker pass on demand so assertions can observe every durable state.
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PhotoImportWorker)));
            services.AddTransient<PhotoImportWorker>();
            services.AddSingleton(vision.Object);
            services.AddSingleton(lookup.Object);
            services.AddSingleton(catalog.Object);
        }));
        using var client = host.CreateClient();
        var student = Guid.NewGuid();
        var other = Guid.NewGuid();
        string Root(Guid learner) => $"/learning/v1/api/vocabulary/students/{learner}/notebook/photo-tasks";
        using var denied = await client.GetAsync(Root(student), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("photo-tasks", catalog: false, learning: true));
        using var upload = new MultipartFormDataContent { { new ByteArrayContent(Photo), "file", "page.jpg" } };
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Root(student), upload, Ct)).StatusCode);
        foreach (var learner in new[] { student, other })
            (await client.PutAsync($"/learning/v1/learners/{learner}", null, Ct)).EnsureSuccessStatusCode();
        using var invalid = new MultipartFormDataContent { { new ByteArrayContent([1, 2]), "file", "bad.jpg" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Root(student), invalid, Ct)).StatusCode);
        using var accepted = await client.PostAsync(Root(student), upload, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await Data(accepted)).GetProperty("id").GetGuid();
        var url = Root(student) + "/" + id;
        Assert.Equal(url, accepted.Headers.Location!.ToString());
        async Task<JsonElement> Detail() => await Data(await client.GetAsync(url, Ct));
        async Task<JsonElement> Add(int index) => await Data(await client.PutAsJsonAsync(url + "/entries/" + index, new { }, Ct));
        var schema = Assert.Single(await host.Services.GetRequiredService<LearningStorage>().PhotoTaskSchemasAsync(Ct));
        async Task Work() => Assert.True(await host.Services.GetRequiredService<PhotoImportWorker>().ProcessOneAsync(schema, Ct));

        Assert.Equal("queued", (await Detail()).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Root(other) + "/" + id, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(url + "/retry", null, Ct)).StatusCode);
        using var anotherCaller = host.CreateClient();
        anotherCaller.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("other-photo-tasks", catalog: false, learning: true));
        (await anotherCaller.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anotherCaller.GetAsync(url, Ct)).StatusCode);

        await Work();
        var failure = await Detail();
        Assert.Equal("failed", failure.GetProperty("state").GetString());
        Assert.Equal("VOCAB_PHOTO_IMPORT_UNAVAILABLE", failure.GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(url + "/retry", null, Ct)).StatusCode);
        await Work();
        var ready = await Detail();
        Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.Equal(2, ready.GetProperty("attempts").GetInt32());
        Assert.Equal(2, ready.GetProperty("entries").GetArrayLength());
        Assert.Equal("matched", ready.GetProperty("entries")[0].GetProperty("candidate").GetProperty("state").GetString());
        var failedAdd = await client.PutAsJsonAsync(url + "/entries/0", new { meaningId = "missing" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, failedAdd.StatusCode);
        Assert.Equal(0, (await Detail()).GetProperty("addedCount").GetInt32());
        var additions = await Task.WhenAll(Add(0), Add(0));
        Assert.True(JsonElement.DeepEquals(additions[0], additions[1]));
        var summary = Assert.Single((await Data(await client.GetAsync(Root(student), Ct))).GetProperty("items").EnumerateArray());
        Assert.Equal(1, summary.GetProperty("addedCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url + "/entries/1", new { word = "pear" }, Ct)).StatusCode);
        var last = await Data(await client.PutAsJsonAsync(url + "/entries/1", new { meaningId = "pear" }, Ct));
        Assert.True(last.GetProperty("taskDeleted").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url, Ct)).StatusCode);
        Assert.Empty((await Data(await client.GetAsync(Root(student), Ct))).GetProperty("items").EnumerateArray());
        var empty = await client.PostAsync(Root(student), upload, Ct);
        await Work();
        var emptyUrl = empty.Headers.Location!;
        var emptyTask = await Data(await client.GetAsync(emptyUrl, Ct));
        Assert.Equal("VOCAB_PHOTO_NO_ENTRIES", emptyTask.GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(emptyUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(emptyUrl + "/retry", null, Ct)).StatusCode);
        Assert.Equal(2, (await Data(await client.GetAsync($"/learning/v1/api/vocabulary/students/{student}/notebook", Ct)))
            .GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task InterruptedClaimsRecoverAndLateResultsCannotResurrectDeletedTasks()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "task-recovery";
        await storage.EnsureAsync(caller, Ct);
        await using var db = new LearningDbContext(storage.Options(caller));
        await using var second = new LearningDbContext(storage.Options(caller));
        var a = new PhotoImportTaskRepository(db);
        var b = new PhotoImportTaskRepository(second);
        var student = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity(id) VALUES ({student})", Ct);
        var id = await a.CreateAsync(student, Photo, Ct);
        var initial = Assert.Single((await Task.WhenAll(a.ClaimAsync(Ct), b.ClaimAsync(Ct))).OfType<PhotoTaskClaim>());
        await db.Database.ExecuteSqlAsync($"UPDATE vocab_photo_task SET updated_at=now()-interval '11 minutes' WHERE id={id}", Ct);
        var recovered = (await b.ClaimAsync(Ct))!;
        Assert.Equal(initial.Id, recovered.Id);
        Assert.Equal(2, recovered.Attempts);
        Assert.Equal(0, await a.FinishAsync(initial, [], "VOCAB_PHOTO_INVALID", Ct));
        Assert.Equal(1, await b.DeleteAsync(student, recovered.Id, Ct));
        Assert.Equal(0, await b.FinishAsync(recovered, [], "VOCAB_PHOTO_INVALID", Ct));
        await a.CreateAsync(student, Photo, Ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM learner_identity WHERE id={student}", Ct);
        Assert.Empty(await a.ListAsync(student, 1, Ct));
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("data");
    }
}
