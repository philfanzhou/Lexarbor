using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Learning.Database;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Calendar;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

/// <summary>
/// How daily learning merges the plan scope with the notebook: order, the word-family quota,
/// when the quota lifts, and how an open slot is refilled. Each test reads the frozen roster.
/// </summary>
public sealed class DailySourceRulesTests : IAsyncDisposable
{
    private const string Caller = "daily-source-rules";
    private static readonly object Student = new { type = "student" };
    private readonly List<IAsyncDisposable> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Enumerable.Reverse(_hosts)) await host.DisposeAsync();
    }

    [Fact]
    public async Task ManualWordsComeFirstThenAThirdForFamilyWordsThenThePlan()
    {
        var learner = await LearnerAsync(new SourceCatalog(Plan("p1", "p2", "p3", "p4", "p5", "p6")), dailyNewWords: 6);
        await learner.AddNotebookAsync(Manual("m1"), Family("f1"), Family("f2"), Family("f3"), Family("f4"));

        await learner.NextNewAsync();

        // max(1, 6 / 3) = 2 family words while plan words remain; each group keeps its own order.
        Assert.Equal(["m1", "f1", "f2", "p1", "p2", "p3"], await learner.RosterAsync());
    }

    [Fact]
    public async Task FamilyWordsAreNotLimitedOnceThePlanHasNothingLeftToLearn()
    {
        var learner = await LearnerAsync(new SourceCatalog(Plan("p1")), dailyNewWords: 6);
        await learner.AddNotebookAsync(Manual("m1"), Family("f1"), Family("f2"), Family("f3"), Family("f4"));
        await learner.AddCardAsync("p1", Meaning("p1"));

        await learner.NextNewAsync();

        Assert.Equal(["m1", "f1", "f2", "f3", "f4"], await learner.RosterAsync());
    }

    [Fact]
    public async Task AFamilyWordThePlanCoversUsesNoQuotaAndIsLearnedAtThePlanLocation()
    {
        var learner = await LearnerAsync(new SourceCatalog(Plan("p1", "p2", "p3")), dailyNewWords: 3);
        await learner.AddNotebookAsync(Family("f1"), Family("f2"), Family("p3", Meaning("p3")));

        await learner.NextNewAsync();

        // max(1, 3 / 3) = 1: f1 takes the family slot and f2 waits. p3 keeps its notebook place among
        // the plan words, ahead of the plan's own order.
        Assert.Equal(["f1", "p3", "p1"], await learner.RosterAsync());
        var covered = Assert.Single((await learner.TasksAsync()).NewWords, word => word.WordKey == "p3");
        Assert.Equal(("book", "one", "p3", (bool?)false), (covered.BookId, covered.UnitId, covered.WordId, covered.UsesFamilyQuota));
    }

    [Fact]
    public async Task AManualWordWithAFamilySourceUsesNoQuota()
    {
        var learner = await LearnerAsync(new SourceCatalog(Plan("p1", "p2", "p3")), dailyNewWords: 3);
        await learner.AddNotebookAsync(Family("m1", familyOnly: false), Family("f1"), Family("f2"));

        await learner.NextNewAsync();

        Assert.Equal(["m1", "f1", "p1"], await learner.RosterAsync());
    }

    [Fact]
    public async Task AnOpenSlotIsRefilledWithinTheQuotaWhilePlanWordsRemain()
    {
        var catalog = new SourceCatalog(Plan("p1", "p2")) { Unavailable = request => request.MeaningId == "p1-m" };
        var learner = await LearnerAsync(catalog, dailyNewWords: 2);
        await learner.AddNotebookAsync(Family("f1"), Family("f2"));

        await learner.NextNewAsync();
        Assert.Equal(["f1", "p1"], await learner.RosterAsync());
        await learner.AnswerAsync(await learner.NextNewAsync());

        // p1 gives no question. f1 already used the only family slot, so p2 takes the open place.
        var next = await learner.NextNewAsync();
        Assert.Equal("p2", next.GetProperty("question").GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task TheQuotaLiftsWhenTheLastPlanWordsGiveNoQuestion()
    {
        var catalog = new SourceCatalog(Plan("p1")) { Unavailable = request => request.MeaningId == "p1-m" };
        var learner = await LearnerAsync(catalog, dailyNewWords: 2);
        await learner.AddNotebookAsync(Family("f1"), Family("f2"));

        await learner.NextNewAsync();
        Assert.Equal(["f1", "p1"], await learner.RosterAsync());
        await learner.AnswerAsync(await learner.NextNewAsync());

        var next = await learner.NextNewAsync();
        Assert.Equal("f2-w", next.GetProperty("question").GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task ExtraLearningUsesTheSameOrderAndQuota()
    {
        var learner = await LearnerAsync(new SourceCatalog(Plan("p1", "p2", "p3", "p4", "p5", "p6")), dailyNewWords: 3);
        await learner.AddNotebookAsync(Manual("m1"));

        // Finish the day: m1 and two plan words.
        for (var answered = 0; answered < 3; answered++) await learner.AnswerAsync(await learner.NextNewAsync());
        await learner.AddNotebookAsync(Manual("m2"), Family("f1"), Family("f2"));

        var extra = await learner.NextExtraAsync();

        Assert.Equal("m2-w", extra.GetProperty("question").GetProperty("prompt").GetString());
        var batch = Assert.Single((await learner.TasksAsync()).ExtraBatches);
        Assert.Equal(["m2", "f1", "p3"], batch.Words.Select(word => word.WordKey).Distinct().ToArray());
    }

    [Fact]
    public async Task ACreatedCardRecordsWhereAndFromWhichSourceItWasLearned()
    {
        var learner = await LearnerAsync(new SourceCatalog(Plan("p1", "p2", "p3")), dailyNewWords: 4);
        await learner.AddNotebookAsync(Manual("m1"), Family("f1"), Family("p3", Meaning("p3")));

        for (var answered = 0; answered < 4; answered++) await learner.AnswerAsync(await learner.NextNewAsync());

        Assert.Equal(new Dictionary<string, (string, string, string, VocabularySourceKind?)>
        {
            ["m1"] = ("other", "m1-w", "m1-other-m", VocabularySourceKind.Notebook),
            ["f1"] = ("other", "f1-w", "f1-other-m", VocabularySourceKind.NotebookFamily),
            // Learned at the plan's location, but it came from the notebook.
            ["p3"] = ("book", "p3", "p3-m", VocabularySourceKind.NotebookFamily),
            ["p1"] = ("book", "p1", "p1-m", VocabularySourceKind.Plan),
        }, await learner.LocationsAsync());
    }

    [Fact]
    public async Task ANotebookWordLearnedAtItsNotebookLocationKeepsTheNotebookSource()
    {
        var catalog = new SourceCatalog(Plan("p1")) { Unavailable = request => request is { BookId: "book", MeaningId: "p1-m" } };
        var learner = await LearnerAsync(catalog, dailyNewWords: 1);
        await learner.AddNotebookAsync(Manual("p1", Meaning("p1")));

        await learner.AnswerAsync(await learner.NextNewAsync());

        Assert.Equal(("other", "p1-w", "p1-other-m", (VocabularySourceKind?)VocabularySourceKind.Notebook),
            (await learner.LocationsAsync())["p1"]);
    }

    [Fact]
    public async Task AReviewUsesTheSavedLocationWhateverQuestionsCameLater()
    {
        var catalog = new SourceCatalog(Plan("p1"));
        var learner = await LearnerAsync(catalog, dailyNewWords: 1);
        await learner.AnswerAsync(await learner.NextNewAsync());
        await learner.AddLaterQuestionAsync("p1", "expired-elsewhere", answered: false);
        await learner.AddLaterQuestionAsync("p1", "answered-elsewhere", answered: true);
        learner.NextDay();

        var review = await learner.NextReviewAsync();

        Assert.Equal("question", review.GetProperty("status").GetString());
        Assert.Equal(("book", "p1-m"), (catalog.Requests.Last().BookId, catalog.Requests.Last().MeaningId));
    }

    [Fact]
    public async Task TheLatestAnsweredLocationStandsInWithoutMovingTheSavedOne()
    {
        var catalog = new SourceCatalog(Plan("p1"));
        var learner = await LearnerAsync(catalog, dailyNewWords: 1);
        await learner.AnswerAsync(await learner.NextNewAsync());
        await learner.AddLaterQuestionAsync("p1", "answered-elsewhere", answered: true);
        await learner.AddLaterQuestionAsync("p1", "expired-elsewhere", answered: false);
        learner.NextDay();
        catalog.Unavailable = request => request.BookId == "book";

        var review = await learner.NextReviewAsync();

        Assert.Equal("question", review.GetProperty("status").GetString());
        Assert.Equal("answered-elsewhere", catalog.Requests.Last().BookId);
        Assert.Equal("book", (await learner.LocationsAsync())["p1"].Item1);
    }

    private static List<LexarborEntry> Plan(params string[] words) => words.Select(word =>
        new LexarborEntry(word, word, word, null, null, word + "-m", "noun", Meaning(word), Meaning(word), null, [new("one", 1, null, null)]))
        .ToList();

    private static string Meaning(string word) => word + " meaning";

    private static NotebookSeed Manual(string word, string? meaning = null) => new(word, meaning ?? Meaning(word), FamilyOnly: false, WithFamily: false);

    private static NotebookSeed Family(string word, string? meaning = null, bool familyOnly = true) =>
        new(word, meaning ?? Meaning(word), familyOnly, WithFamily: true);

    private async Task<Learner> LearnerAsync(SourceCatalog catalog, int dailyNewWords)
    {
        var clock = new Clock();
        var factory = new VocabularyWebApplicationFactory();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILexarborClient>(catalog);
            services.AddSingleton<IVocabularyNotebookCatalog>(new EmptyNotebookCatalog());
            services.AddSingleton<TimeProvider>(clock);
        }));
        _hosts.Add(factory);
        _hosts.Add(host);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync(Caller, catalog: false, learning: true));
        var student = Guid.NewGuid();
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        var learner = new Learner(host, client, student, clock);
        await Data(await client.PostAsJsonAsync($"{learner.Root}/plans",
            new { actor = Student, bookId = "book", unitIds = new[] { "one" }, dailyNewWords }, Ct));
        return learner;
    }

    private sealed record NotebookSeed(string Word, string Meaning, bool FamilyOnly, bool WithFamily);

    /// <summary>The server's learning calendar follows this clock, so a test can move to the next day.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Learner(WebApplicationFactory<Program> host, HttpClient client, Guid student, Clock clock)
    {
        private DateOnly Day => host.Services.GetRequiredService<LearningCalendar>().Today(clock);

        private string Today => Day.ToString("yyyy-MM-dd");
        private DateTimeOffset _addedAt = DateTimeOffset.UtcNow.AddHours(-1);

        public string Root { get; } = $"/learning/v1/api/vocabulary/students/{student}";

        public async Task AddNotebookAsync(params NotebookSeed[] seeds)
        {
            await using var db = Open();
            var notebook = new VocabNotebookRepository(db);
            foreach (var seed in seeds)
            {
                _addedAt = _addedAt.AddSeconds(1);
                var item = new VocabNotebookItemModel
                {
                    Id = Guid.NewGuid(),
                    StudentId = student,
                    WordKey = seed.Word,
                    MeaningKey = seed.Meaning,
                    Word = seed.Word,
                    Meaning = seed.Meaning,
                    Status = VocabNotebookItemStatus.Ready,
                    BookId = "other",
                    WordId = seed.Word + "-w",
                    MeaningId = seed.Word + "-other-m",
                    FamilyOnly = seed.FamilyOnly,
                    AddedBy = VocabNotebookActorType.Student,
                    AddedAt = _addedAt,
                    UpdatedAt = _addedAt,
                };
                if (seed.WithFamily) item.AttachFamily(new("root", "root", "root-w", "derived_noun", "outgoing"));
                await notebook.AddAsync(item, Ct);
            }
            await db.SaveChangesAsync(Ct);
        }

        public async Task AddCardAsync(string word, string meaning)
        {
            await using var db = Open();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vocab_memory_card(student_id, word_key, meaning_key, state, due_at, reps, lapses, last_reviewed_at,
                    first_learned_at, version)
                VALUES ({student}, {word}, {meaning}, 2, now() + interval '30 days', 1, 0, now(), now(), 1)
                """, Ct);
        }

        public Task<JsonElement> NextNewAsync() => NextAsync("new");

        public Task<JsonElement> NextExtraAsync() => NextAsync("extra");

        public async Task AnswerAsync(JsonElement next)
        {
            var question = next.GetProperty("question");
            var option = question.GetProperty("options").EnumerateArray().First().GetProperty("optionId").GetGuid();
            await Data(await client.PostAsJsonAsync($"/learning/v1/api/vocabulary/questions/{question.GetProperty("id").GetGuid()}/answer",
                new { studentId = student, optionId = option, elapsedMs = 800, localDate = Today, tzOffsetMinutes = 0 }, Ct));
        }

        public async Task<VocabDailyTasks> TasksAsync()
        {
            await using var db = Open();
            var plan = await db.VocabPlans.AsNoTracking().SingleAsync(row => row.StudentId == student, Ct);
            var day = await new VocabPlanDayRepository(db).FindAsync(plan.Id, Day, Ct);
            return day!.Tasks!;
        }

        /// <summary>The distinct words of today's new-word roster, in order.</summary>
        public async Task<string[]> RosterAsync() =>
            (await TasksAsync()).NewWords.Select(word => word.WordKey).Distinct().ToArray();

        public async Task<Dictionary<string, (string, string, string, VocabularySourceKind?)>> LocationsAsync()
        {
            await using var db = Open();
            return await db.VocabCardLocations.AsNoTracking().Where(row => row.StudentId == student)
                .ToDictionaryAsync(row => row.WordKey,
                    row => (row.BookId, row.WordId, row.MeaningId, (VocabularySourceKind?)row.Source), Ct);
        }

        /// <summary>A question for the word after it was learned, at another book.</summary>
        public async Task AddLaterQuestionAsync(string word, string bookId, bool answered)
        {
            await using var db = Open();
            var option = Guid.NewGuid();
            var options = JsonSerializer.Serialize(new[] { new { optionId = option.ToString("D"), text = "right" } });
            DateTimeOffset? answeredAt = answered ? DateTimeOffset.UtcNow.AddMinutes(1) : null;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vocab_issued_question(id, student_id, kind, word_key, meaning_key, word_id, meaning_id, book_id,
                    direction, prompt, options_snapshot, correct_option_id, issued_at, expires_at, answered_at)
                VALUES ({Guid.NewGuid()}, {student}, 2, {word}, {Meaning(word)}, {word + "-x"}, {word + "-x-m"}, {bookId},
                    1, {word}, {options}, {option}, now() + interval '1 minute', now() + interval '2 minutes', {answeredAt})
                """, Ct);
            if (!answered)
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE vocab_issued_question SET expires_at = now() - interval '1 minute' WHERE book_id = {bookId}", Ct);
        }

        /// <summary>Moves to the next learning day, whose daily reviews include every learned card.</summary>
        public void NextDay() => clock.Now = clock.Now.AddDays(1);

        public Task<JsonElement> NextReviewAsync() => NextAsync("review");

        private async Task<JsonElement> NextAsync(string mode) =>
            await Data(await client.PostAsJsonAsync($"{Root}/questions/next?localDate={Today}&tzOffsetMinutes=0", new { mode }, Ct));

        private LearningDbContext Open() =>
            new(host.Services.GetRequiredService<LearningStorage>().Options(Caller));
    }

    private sealed class SourceCatalog(List<LexarborEntry> entries) : ILexarborClient
    {
        public Func<LexarborLearningQuestionRequest, bool> Unavailable { get; set; } = _ => false;
        public ConcurrentQueue<LexarborLearningQuestionRequest> Requests { get; } = new();

        public Task<LexarborEntryPage> GetEntriesAsync(string bookId, string? unitId, int page, int size, CancellationToken cancellationToken)
        {
            Assert.Equal("book", bookId);
            return Task.FromResult(new LexarborEntryPage(entries, 1, entries.Count, entries.Count));
        }

        public Task<IReadOnlyList<LexarborUnit>> GetUnitsAsync(string bookId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LexarborUnit>>([new LexarborUnit("one", 1, null, entries.Count, entries.Count)]);

        public Task<LexarborAudio> GetAudioAsync(string wordId, string bookId, string meaningId, string locale, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VocabGeneratedQuestion?> GetLearningQuestionAsync(LexarborLearningQuestionRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (Unavailable(request)) return Task.FromResult<VocabGeneratedQuestion?>(null);
            return Task.FromResult<VocabGeneratedQuestion?>(new(VocabQuestionDirection.EnglishToChinese, request.WordId,
                [new("right", true), new("wrong one", false), new("wrong two", false), new("wrong three", false)]));
        }
    }

    private sealed class EmptyNotebookCatalog : IVocabularyNotebookCatalog
    {
        public Task<IReadOnlyList<VocabCatalogMeaning>> SearchAsync(string keyword, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VocabCatalogMeaning>>([]);

        public Task<VocabMeaningSubmission> SubmitAsync(string word, string? partOfSpeech, string meaning, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VocabMeaningSubmission>> LookupAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VocabMeaningSubmission>>([]);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }
}
