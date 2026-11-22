using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Learning.Database;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Lexarbor.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
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
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT (version=5 AND store_id={storeId} AND generation={generation}) AS \"Value\" FROM learning_schema_version").SingleAsync(Ct));
        Assert.True(await db.Learners.AnyAsync(s => s.Id == student, Ct));
        Assert.Empty(await db.UnitPracticeSessions.ToListAsync(Ct));
    }

    [Fact]
    public async Task ChoiceSessionsAskGeneratedQuestionsAndRetryWithFreshOnes()
    {
        var catalog = new PracticeCatalog();
        catalog.Questions = request => request.WordId switch
        {
            // The unit lacks distractors for apple, so the whole book supplies them; pear gets none and is spelled.
            "apple" when request.UnitId == null => Choice(VocabQuestionDirection.ChineseToEnglish, "苹果", "apple"),
            _ => null
        };
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILexarborClient>(catalog)));
        var (client, path) = await LearnerAsync(host, "choice-practice");
        var session = Guid.NewGuid();
        var start = new { sessionId = session, bookId = "book", unitId = "one", mode = "choice" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path,
            new { sessionId = session, bookId = "book", unitId = "one", mode = "Choice" }, Ct)).StatusCode);

        var view = await Data(await client.PostAsJsonAsync(path, start, Ct));
        Assert.Equal("choice", view.GetProperty("mode").GetString());
        Assert.All(catalog.Requests, request =>
        {
            Assert.Equal("review", request.Mode);
            Assert.Equal(new[] { "ChineseToEnglish" }, request.Types!);
            Assert.True(request.SameEntryKind);
            Assert.Null(request.Relation);
        });
        Assert.Equal(new string?[] { null, "one" }, catalog.Requests.Where(r => r.WordId == "apple").Select(r => r.UnitId).Order());
        var question = view.GetProperty("nextQuestion");
        Assert.Equal("ChineseToEnglish", question.GetProperty("type").GetString());
        Assert.Equal("苹果", question.GetProperty("prompt").GetString());
        Assert.False(question.TryGetProperty("meaning", out _));
        Assert.DoesNotContain("isCorrect", question.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var options = Options(question);
        Assert.Equal(4, options.Count);

        catalog.Available = false;
        Assert.Equal(question.GetRawText(), (await Data(await client.PostAsJsonAsync(path, start, Ct))).GetProperty("nextQuestion").GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path,
            new { sessionId = session, bookId = "book", unitId = "one", mode = "spelling" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { sessionId = session, bookId = "book", unitId = "one" }, Ct)).StatusCode);

        var answers = $"{path}/{session}/answers";
        var id = question.GetProperty("questionId").GetGuid();
        foreach (var invalid in new object[]
        {
            new { questionId = id, answer = "apple" },
            new { questionId = id, optionId = Guid.NewGuid() },
            new { questionId = id, letters = new[] { "a" } },
            new { questionId = id, optionId = options["pear"], unknown = true }
        })
        {
            var response = await client.PostAsJsonAsync(answers, invalid, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("VOCAB_PRACTICE_ANSWER_INVALID", await response.Content.ReadAsStringAsync(Ct));
        }
        var feedback = await Data(await client.PostAsJsonAsync(answers, new { questionId = id, optionId = options["pear"] }, Ct));
        Assert.False(feedback.GetProperty("correct").GetBoolean());
        Assert.Equal(options["apple"], feedback.GetProperty("correctOptionId").GetGuid());
        Assert.Equal("apple", feedback.GetProperty("target").GetProperty("word").GetString());

        question = (await Data(await client.GetAsync(path, Ct))).GetProperty("nextQuestion");
        Assert.Equal("Spelling", question.GetProperty("type").GetString());
        Assert.Equal("梨", question.GetProperty("meaning").GetString());
        feedback = await Data(await client.PostAsJsonAsync(answers, new { questionId = question.GetProperty("questionId").GetGuid(), answer = "pear" }, Ct));
        Assert.True(feedback.GetProperty("correct").GetBoolean());
        Assert.False(feedback.TryGetProperty("correctOptionId", out _));

        catalog.Available = true;
        catalog.Questions = request => Choice(VocabQuestionDirection.ChineseToEnglish, "苹果", "apple");
        var retry = await Data(await client.PutAsync($"{path}/{session}/rounds/2", null, Ct));
        Assert.Equal(1, retry.GetProperty("roundTotal").GetInt32());
        var retried = retry.GetProperty("nextQuestion");
        Assert.NotEqual(id, retried.GetProperty("questionId").GetGuid());
        Assert.Empty(Options(retried).Values.Intersect(options.Values));
        Assert.Equal(retry.GetRawText(), (await Data(await client.PutAsync($"{path}/{session}/rounds/2", null, Ct))).GetRawText());
        await Data(await client.PostAsJsonAsync(answers, new { questionId = retried.GetProperty("questionId").GetGuid(), unknown = true }, Ct));

        // Without the generator, the next retry reissues the first-pass question with new option IDs.
        catalog.QuestionsAvailable = false;
        retry = await Data(await client.PutAsync($"{path}/{session}/rounds/3", null, Ct));
        var reissued = retry.GetProperty("nextQuestion");
        Assert.Equal("ChineseToEnglish", reissued.GetProperty("type").GetString());
        Assert.Equal(options.Keys.Order(), Options(reissued).Keys.Order());
        Assert.Empty(Options(reissued).Values.Intersect(options.Values));
        feedback = await Data(await client.PostAsJsonAsync(answers,
            new { questionId = reissued.GetProperty("questionId").GetGuid(), optionId = Options(reissued)["apple"] }, Ct));
        Assert.True(feedback.GetProperty("correct").GetBoolean());
        view = await Data(await client.GetAsync(path, Ct));
        Assert.True(view.GetProperty("mistakes")[0].GetProperty("corrected").GetBoolean());
        Assert.Equal(1, view.GetProperty("firstPassCorrect").GetInt32());
    }

    [Fact]
    public async Task MixedSessionsUseTheReviewDrawAndGradeLetters()
    {
        var catalog = new PracticeCatalog
        {
            Questions = request => request.WordId == "apple"
                ? new VocabGeneratedQuestion(VocabQuestionDirection.FillLetters, "苹果", [], ["a", null, "p", null, "e"], "apple")
                : Choice(VocabQuestionDirection.EnglishToChinese, "pear", "梨")
        };
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILexarborClient>(catalog)));
        var (client, path) = await LearnerAsync(host, "mixed-practice");
        var session = Guid.NewGuid();
        var view = await Data(await client.PostAsJsonAsync(path, new { sessionId = session, bookId = "book", unitId = "one", mode = "mixed" }, Ct));
        Assert.Equal("mixed", view.GetProperty("mode").GetString());
        Assert.All(catalog.Requests, request => Assert.Null(request.Types));
        Assert.All(catalog.Requests, request => Assert.Equal("one", request.UnitId));
        var question = view.GetProperty("nextQuestion");
        Assert.Equal("FillLetters", question.GetProperty("type").GetString());
        Assert.Equal(5, question.GetProperty("pattern").GetArrayLength());
        Assert.False(question.TryGetProperty("options", out _));
        var answers = $"{path}/{session}/answers";
        var id = question.GetProperty("questionId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(answers, new { questionId = id, letters = new[] { "p" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(answers, new { questionId = id, letters = Array.Empty<string>() }, Ct)).StatusCode);
        Assert.True((await Data(await client.PostAsJsonAsync(answers, new { questionId = id, letters = new[] { "P", "l" } }, Ct))).GetProperty("correct").GetBoolean());
        question = (await Data(await client.GetAsync(path, Ct))).GetProperty("nextQuestion");
        Assert.Equal("EnglishToChinese", question.GetProperty("type").GetString());
        Assert.Equal("pear", question.GetProperty("prompt").GetString());

        catalog.QuestionsAvailable = false;
        var replace = new { sessionId = Guid.NewGuid(), previousSessionId = session, bookId = "book", unitId = "one", mode = "mixed" };
        var unavailable = await client.PostAsJsonAsync(path, replace, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(session, (await Data(await client.GetAsync(path, Ct))).GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task VersionThreeUpgradeKeepsSessionsAsSpelling()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "unit-practice-modes-upgrade";
        var student = Guid.NewGuid();
        var session = Guid.NewGuid();
        await using var db = new LearningDbContext(storage.Options(caller));
        var createSchema = "CREATE SCHEMA " + LearningStorage.Schema(caller);
        await db.Database.ExecuteSqlRawAsync(createSchema, Ct);
        foreach (var file in new[] { "SchemaV1.sql", "ArchiveV2.sql", "UnitPracticeV3.sql" })
        {
            using var stream = typeof(LearningStorage).Assembly.GetManifestResourceStream("Lexarbor.Database.Learning." + file)!;
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(Ct), Ct);
            if (file == "SchemaV1.sql")
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE learning_schema_version (version integer PRIMARY KEY, store_id uuid NOT NULL DEFAULT gen_random_uuid(), generation uuid); INSERT INTO learning_schema_version (version) VALUES (1)", Ct);
        }
        await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity(id) VALUES ({student})", Ct);
        await db.Database.ExecuteSqlAsync($"INSERT INTO vocab_unit_practice(student_id, session_id, book_id, unit_id) VALUES ({student}, {session}, 'book', 'one')", Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_unit_practice_item(session_id, position, word_id, meaning_id, word, meaning, first_question_id)
            VALUES ({session}, 1, 'apple', 'apple-m', 'apple', '苹果', {Guid.NewGuid()})
            """, Ct);
        await storage.EnsureAsync(caller, Ct);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT (version=5) AS \"Value\" FROM learning_schema_version").SingleAsync(Ct));
        var saved = await db.UnitPracticeSessions.SingleAsync(Ct);
        Assert.Equal("spelling", saved.Mode);
        var progress = await new UnitPracticeRepository(db).ProgressAsync(saved, Ct);
        Assert.Equal(UnitPracticeMode.Spelling, progress.Mode);
        Assert.Equal("Spelling", progress.NextQuestion!.Type);
        Assert.Equal("苹果", progress.NextQuestion.Meaning);
    }

    private static async Task<(HttpClient Client, string Path)> LearnerAsync(WebApplicationFactory<Program> host, string caller)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync(caller, catalog: false, learning: true));
        var student = Guid.NewGuid();
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        return (client, $"/learning/v1/api/vocabulary/students/{student}/unit-practice");
    }

    private static Dictionary<string, Guid> Options(JsonElement question) => question.GetProperty("options").EnumerateArray()
        .ToDictionary(option => option.GetProperty("text").GetString()!, option => option.GetProperty("optionId").GetGuid());

    private static VocabGeneratedQuestion Choice(VocabQuestionDirection direction, string prompt, string answer) =>
        new(direction, prompt, [new(answer, true), .. new[] { "pear", "plum", "grape", "melon" }.Where(text => text != answer).Take(3)
            .Select(text => new VocabGeneratedOption(text, false))]);

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("data");
    }

    private sealed class PracticeCatalog : ILexarborClient
    {
        public bool Available { get; set; } = true;
        public bool QuestionsAvailable { get; set; } = true;
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
        public Func<LexarborLearningQuestionRequest, VocabGeneratedQuestion?>? Questions { get; set; }
        public ConcurrentQueue<LexarborLearningQuestionRequest> Requests { get; } = new();
        public Task<VocabGeneratedQuestion?> GetLearningQuestionAsync(LexarborLearningQuestionRequest request, CancellationToken cancellationToken)
        {
            if (Questions == null) throw new NotSupportedException();
            if (!Available || !QuestionsAvailable) throw new VocabCatalogUnavailableException();
            Requests.Enqueue(request);
            return Task.FromResult(Questions(request));
        }
    }
}
