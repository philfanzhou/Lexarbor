using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Learning.Database;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Lexarbor.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class UnitPracticeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnitPracticeResumesAcrossDevicesAndKeepsFirstPassIndependentOfCorrections()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var catalog = new PracticeCatalog();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILexarborClient>(catalog)));
        using var phone = host.CreateClient();
        using var tablet = host.CreateClient();
        var key = await host.SeedKeyAsync("practice", catalog: false, learning: true);
        phone.DefaultRequestHeaders.Add("X-Api-Key", key);
        tablet.DefaultRequestHeaders.Add("X-Api-Key", key);
        var student = Guid.NewGuid();
        var session = Guid.NewGuid();
        var path = $"/learning/v1/api/vocabulary/students/{student}/unit-practice";
        var learner = $"/learning/v1/learners/{student}";
        var start = new { sessionId = session, bookId = "book", unitId = "one" };
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync(path, Ct)).StatusCode);
        (await phone.PutAsync(learner, null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await Data(await phone.GetAsync(path, Ct))).ValueKind);

        var view = await Data(await phone.PostAsJsonAsync(path, start, Ct));
        Assert.Equal(2, view.GetProperty("total").GetInt32());
        var first = view.GetProperty("nextQuestion");
        Assert.Equal("苹果", first.GetProperty("meaning").GetString());
        Assert.DoesNotContain("apple", first.GetRawText(), StringComparison.OrdinalIgnoreCase);
        catalog.Available = false;
        Assert.Equal(first.GetRawText(), (await Data(await tablet.PostAsJsonAsync(path, start, Ct)))
            .GetProperty("nextQuestion").GetRawText());
        var answers = $"{path}/{session}/answers";
        var firstId = first.GetProperty("questionId").GetGuid();
        var answer = new { questionId = firstId, answer = "pear" };
        var duplicates = await Task.WhenAll(phone.PostAsJsonAsync(answers, answer, Ct), tablet.PostAsJsonAsync(answers, answer, Ct));
        foreach (var response in duplicates) Assert.False((await Data(response)).GetProperty("correct").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PutAsync($"{path}/{session}/rounds/2", null, Ct)).StatusCode);
        view = await Data(await tablet.GetAsync(path, Ct));
        using var otherCaller = host.CreateClient();
        otherCaller.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("other-practice", catalog: false, learning: true));
        (await otherCaller.PutAsync(learner, null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await Data(await otherCaller.GetAsync(path, Ct))).ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await otherCaller.PostAsJsonAsync(answers, answer, Ct)).StatusCode);
        using var catalogOnly = host.CreateClient();
        await catalogOnly.UseCatalogKeyAsync(host);
        Assert.Equal(HttpStatusCode.Forbidden, (await catalogOnly.GetAsync(path, Ct)).StatusCode);
        Assert.Equal(1, view.GetProperty("answered").GetInt32());
        var secondId = view.GetProperty("nextQuestion").GetProperty("questionId").GetGuid();
        await Data(await tablet.PostAsJsonAsync(answers, new { questionId = secondId, answer = " PEAR " }, Ct));
        view = await Data(await phone.GetAsync(path, Ct));
        Assert.Equal(1, view.GetProperty("firstPassCorrect").GetInt32());
        Assert.Single(view.GetProperty("mistakes").EnumerateArray());
        var retry = await Data(await phone.PutAsync($"{path}/{session}/rounds/2", null, Ct));
        Assert.Equal(retry.GetRawText(), (await Data(await tablet.PutAsync($"{path}/{session}/rounds/2", null, Ct))).GetRawText());
        var retryId = retry.GetProperty("nextQuestion").GetProperty("questionId").GetGuid();
        Assert.NotEqual(firstId, retryId);
        await Data(await phone.PostAsJsonAsync(answers, new { questionId = retryId, unknown = true }, Ct));
        retry = await Data(await phone.PutAsync($"{path}/{session}/rounds/3", null, Ct));
        await Data(await tablet.PostAsJsonAsync(answers, new { questionId = retry.GetProperty("nextQuestion").GetProperty("questionId").GetGuid(), answer = "apple" }, Ct));
        view = await Data(await phone.GetAsync(path, Ct));
        Assert.True(view.GetProperty("mistakes")[0].GetProperty("corrected").GetBoolean());
        Assert.Equal(1, view.GetProperty("firstPassCorrect").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PutAsync($"{path}/{session}/rounds/4", null, Ct)).StatusCode);

        var nextSession = Guid.NewGuid();
        var replace = new { sessionId = nextSession, previousSessionId = session, bookId = "book", unitId = "one" };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await tablet.PostAsJsonAsync(path, replace, Ct)).StatusCode);
        Assert.Equal(session, (await Data(await phone.GetAsync(path, Ct))).GetProperty("sessionId").GetGuid());
        catalog.Available = true;
        await Data(await tablet.PostAsJsonAsync(path, replace, Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PostAsJsonAsync(path, start, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsJsonAsync(answers, answer, Ct)).StatusCode);

        var storage = host.Services.GetRequiredService<LearningStorage>();
        await using var db = new LearningDbContext(storage.Options("practice"));
        Assert.Empty(await db.VocabPlans.ToListAsync(Ct));
        Assert.Empty(await db.VocabPlanDays.ToListAsync(Ct));
        Assert.Empty(await db.VocabAttempts.ToListAsync(Ct));
        Assert.Empty(await db.MemoryCards.ToListAsync(Ct));
        Assert.Empty(await db.MemoryReviewLogs.ToListAsync(Ct));
        Assert.Empty(await db.VocabIssuedQuestions.ToListAsync(Ct));
        Assert.Empty(await db.VocabNotebookItems.ToListAsync(Ct));
        (await phone.DeleteAsync(learner, Ct)).EnsureSuccessStatusCode();
        Assert.Empty(await db.UnitPracticeItems.ToListAsync(Ct));
    }

    [Fact]
    public async Task VersionTwoUpgradePreservesExistingLearnerAndStoreBinding()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "unit-practice-upgrade";
        var student = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var generation = Guid.NewGuid();
        await using var db = new LearningDbContext(storage.Options(caller));
        var createSchema = "CREATE SCHEMA " + LearningStorage.Schema(caller);
        await db.Database.ExecuteSqlRawAsync(createSchema, Ct);
        foreach (var file in new[] { "SchemaV1.sql", "ArchiveV2.sql" })
        {
            using var stream = typeof(LearningStorage).Assembly.GetManifestResourceStream("Lexarbor.Database.Learning." + file)!;
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(Ct), Ct);
            if (file == "SchemaV1.sql")
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE learning_schema_version (version integer PRIMARY KEY, store_id uuid, generation uuid)", Ct);
                await db.Database.ExecuteSqlAsync($"INSERT INTO learning_schema_version VALUES (1, {storeId}, {generation})", Ct);
            }
        }
        await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity(id) VALUES ({student})", Ct);
        await storage.EnsureAsync(caller, Ct);
        await new LearningStorage(database.ConnectionString).EnsureAsync(caller, Ct);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT (version=3 AND store_id={storeId} AND generation={generation}) AS \"Value\" FROM learning_schema_version").SingleAsync(Ct));
        Assert.True(await db.Learners.AnyAsync(s => s.Id == student, Ct));
        Assert.Empty(await db.UnitPracticeSessions.ToListAsync(Ct));
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("data");
    }

    private sealed class PracticeCatalog : ILexarborClient
    {
        public bool Available { get; set; } = true;
        public Task<LexarborEntryPage> GetEntriesAsync(string bookId, string? unitId, int page, int size, CancellationToken cancellationToken)
        {
            if (!Available) throw new LexarborCatalogException(VocabularyCatalogErrorCodes.CatalogUnavailable, true, "Catalog offline.");
            Assert.Equal("book", bookId);
            Assert.Equal("one", unitId);
            var apple = Entry("apple", "苹果", "one");
            List<LexarborEntry> entries = page == 1 ? [apple, Entry("apple", "别的义项", "two")] : [apple, Entry("pear", "梨", "one")];
            return Task.FromResult(new LexarborEntryPage(entries, 2, 4, 2));
        }

        private static LexarborEntry Entry(string word, string meaning, string unit) =>
            new(word, word, word, null, null, word + meaning, "noun", meaning, meaning, null, [new(unit, 1, null, null)]);
        public Task<IReadOnlyList<LexarborUnit>> GetUnitsAsync(string bookId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LexarborAudio> GetAudioAsync(string wordId, string bookId, string meaningId, string locale, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VocabGeneratedQuestion?> GetLearningQuestionAsync(LexarborLearningQuestionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
