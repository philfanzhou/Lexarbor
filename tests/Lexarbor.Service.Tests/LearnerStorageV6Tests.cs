using System.Text.Json;
using Lexarbor.Learning.Database;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

public sealed class LearnerStorageV6Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task VersionFiveUpgradeGivesEachCardTheLocationOfTheQuestionThatCreatedIt()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "card-location-upgrade";
        await using var db = new LearningDbContext(storage.Options(caller));
        var createSchema = "CREATE SCHEMA " + LearningStorage.Schema(caller);
        await db.Database.ExecuteSqlRawAsync(createSchema, Ct);
        foreach (var file in new[] { "SchemaV1.sql", "ArchiveV2.sql", "UnitPracticeV3.sql", "UnitPracticeModesV4.sql", "StorageCleanupV5.sql" })
        {
            using var stream = typeof(LearningStorage).Assembly.GetManifestResourceStream("Lexarbor.Database.Learning." + file)!;
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(Ct), Ct);
            if (file == "SchemaV1.sql")
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE learning_schema_version (version integer PRIMARY KEY, store_id uuid NOT NULL DEFAULT gen_random_uuid(), generation uuid); INSERT INTO learning_schema_version (version) VALUES (1)", Ct);
        }

        var student = Guid.NewGuid();
        var plan = Guid.NewGuid();
        const string segments = """[{"bookId":"book","unitIds":null}]""";
        await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity(id) VALUES ({student})", Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_plan(id, student_id, segments, daily_new_words, controller, status, start_date, total_words, version, created_at, updated_at)
            VALUES ({plan}, {student}, {segments}, 10, 1, 1, '2026-01-01', 0, 1, now(), now())
            """, Ct);
        foreach (var word in new[] { "apple", "pear", "plum" })
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vocab_memory_card(student_id, word_key, meaning_key, state, due_at, reps, lapses, last_reviewed_at, first_learned_at, version)
                VALUES ({student}, {word}, {word + "-key"}, 2, now(), 1, 0, now(), now(), 1)
                """, Ct);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // apple was learned at "first"; a later review moved the old "latest question" rule to "review" and then "open".
        var created = await AddQuestionAsync(db, student, plan, 1, "apple", "first", start, answered: true);
        await AddQuestionAsync(db, student, plan, 2, "apple", "review", start.AddDays(1), answered: true);
        await AddQuestionAsync(db, student, plan, 2, "apple", "open", start.AddDays(2), answered: false);
        // pear has only reviews, for example from an imported history: the latest answered one wins.
        await AddQuestionAsync(db, student, plan, 2, "pear", "older", start, answered: true);
        var recent = await AddQuestionAsync(db, student, plan, 2, "pear", "recent", start.AddDays(1), answered: true);
        await AddQuestionAsync(db, student, plan, 2, "pear", "unanswered", start.AddDays(2), answered: false);

        await storage.EnsureAsync(caller, Ct);
        await new LearningStorage(database.ConnectionString).EnsureAsync(caller, Ct);

        Assert.Equal(7, await db.Database.SqlQuery<int>($"SELECT version AS \"Value\" FROM learning_schema_version").SingleAsync(Ct));
        var rows = await db.VocabCardLocations.AsNoTracking().OrderBy(row => row.WordKey).ToListAsync(Ct);
        Assert.Equal(
            [("apple", "first", "apple-first", (short?)null, (Guid?)created, start.AddMinutes(1)),
                ("pear", "recent", "pear-recent", null, recent, start.AddDays(1).AddMinutes(1))],
            rows.Select(row => (row.WordKey, row.BookId, row.WordId, row.Source, row.SourceQuestionId, row.RecordedAt)).ToArray());
        // plum has no answered question, so it keeps the read-time fallback.
        Assert.DoesNotContain(rows, row => row.WordKey == "plum");

        await db.Database.ExecuteSqlAsync($"DELETE FROM vocab_memory_card WHERE word_key = 'apple'", Ct);
        Assert.Equal(["pear"], await db.VocabCardLocations.Select(row => row.WordKey).ToListAsync(Ct));
    }

    private static async Task<Guid> AddQuestionAsync(LearningDbContext db, Guid student, Guid plan, int kind, string word,
        string bookId, DateTimeOffset issuedAt, bool answered)
    {
        var id = Guid.NewGuid();
        var option = Guid.NewGuid();
        var options = JsonSerializer.Serialize(new[] { new { optionId = option.ToString("D"), text = "right" } });
        DateTimeOffset? answeredAt = answered ? issuedAt.AddMinutes(1) : null;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_issued_question(id, student_id, plan_id, kind, word_key, meaning_key, word_id, meaning_id, book_id,
                direction, prompt, options_snapshot, correct_option_id, issued_at, expires_at, answered_at)
            VALUES ({id}, {student}, {plan}, {kind}, {word}, {word + "-key"}, {word + "-" + bookId}, {word + "-m"}, {bookId},
                1, {word}, {options}, {option}, {issuedAt}, {issuedAt.AddHours(1)}, {answeredAt})
            """, Ct);
        return id;
    }
}
