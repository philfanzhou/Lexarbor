using Lexarbor.Learning.Database;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Service.Tests;

public sealed class LearnerStorageV5Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Student = Guid.NewGuid();
    private static readonly Guid LegacyUnits = Guid.NewGuid();
    private static readonly Guid LegacyBook = Guid.NewGuid();
    private static readonly Guid LegacyEmptyUnits = Guid.NewGuid();
    private static readonly Guid MultiBook = Guid.NewGuid();
    private static readonly Guid ReviewOnly = Guid.NewGuid();
    private static readonly Guid BlankSegments = Guid.NewGuid();
    private static readonly Guid WordLevel = Guid.NewGuid();
    private static readonly Guid MeaningPair = Guid.NewGuid();
    private static readonly Guid AlreadyMoved = Guid.NewGuid();
    private static readonly Guid Plain = Guid.NewGuid();

    [Fact]
    public async Task VersionFourUpgradeMovesLegacyScopesAndFamilySourcesWithoutChangingWhatReadersSee()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "storage-cleanup-upgrade";
        await using var db = await VersionFourAsync(storage, caller);
        await SeedLegacyRowsAsync(db);

        await storage.EnsureAsync(caller, Ct);
        await new LearningStorage(database.ConnectionString).EnsureAsync(caller, Ct);

        Assert.Equal(6, await db.Database.SqlQuery<int>($"SELECT version AS \"Value\" FROM learning_schema_version").SingleAsync(Ct));
        Assert.Empty(await db.Database.SqlQuery<string>($"""
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE table_schema = current_schema()
                AND ((table_name = 'vocab_plan' AND column_name IN ('book_id', 'unit_ids'))
                    OR (table_name = 'vocab_notebook_item' AND column_name LIKE 'family\_%' AND column_name <> 'family_only'))
            """).ToListAsync(Ct));
        Assert.Equal(3, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::integer AS "Value" FROM pg_indexes WHERE schemaname = current_schema()
                AND indexname IN ('ix_vocab_issued_question_plan_open', 'ix_vocab_issued_question_student_open',
                    'ix_vocab_issued_question_student_card')
            """).SingleAsync(Ct));

        var plans = new VocabPlanRepository(db);
        Assert.Equal("legacy-book[u1,u2]", Scope(await plans.FindByIdAsync(LegacyUnits, Ct)));
        Assert.Equal("legacy-book[*]", Scope(await plans.FindByIdAsync(LegacyBook, Ct)));
        Assert.Equal("legacy-book[*]", Scope(await plans.FindByIdAsync(LegacyEmptyUnits, Ct)));
        Assert.Equal("a[*] b[x]", Scope(await plans.FindByIdAsync(MultiBook, Ct)));
        Assert.Equal("", Scope(await plans.FindByIdAsync(ReviewOnly, Ct)));
        Assert.Equal("", Scope(await plans.FindByIdAsync(BlankSegments, Ct)));

        var items = (await new VocabNotebookRepository(db).ListAsync(Student, Ct)).ToDictionary(item => item.Id);
        var word = Assert.Single(items[WordLevel].FamilySources);
        Assert.Equal(new VocabNotebookFamilySource("happy", "happy", "happy-w", "derived_noun", "outgoing",
            Target: new("happiness-w", "book", "happiness-m")), word);
        Assert.True(items[WordLevel].FamilyOnly);
        var pair = Assert.Single(items[MeaningPair].FamilySources);
        Assert.Equal(new VocabNotebookFamilySource("quick", "quick", "quick-w", "derived_adverb", "incoming",
            "rel-1", "quick-m", "book", new("quickly-w", "book", "quickly-m")), pair);
        // An item that already had source rows was read from them alone; its old item columns are not copied.
        Assert.Equal("table-source", Assert.Single(items[AlreadyMoved].FamilySources).Word);
        Assert.Empty(items[Plain].FamilySources);
    }

    [Fact]
    public async Task FamilyRulesOfTheDroppedItemCheckStillHoldAtCommit()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "storage-cleanup-family";
        await storage.EnsureAsync(caller, Ct);
        await using var db = new LearningDbContext(storage.Options(caller));
        await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity(id) VALUES ({Student})", Ct);

        // A family-only item needs a source.
        await AssertFamilyViolationAsync(db.Database.ExecuteSqlAsync(ReadyItem(Guid.NewGuid(), "lonely", familyOnly: true), Ct));
        // A source belongs to a ready item and names another word.
        var ready = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync(ReadyItem(ready, "kindness", familyOnly: false), Ct);
        await AssertFamilyViolationAsync(db.Database.ExecuteSqlAsync(Source(ready, "kindness"), Ct));
        var pending = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_notebook_item(id, student_id, word_key, meaning_key, word, meaning, status, submission_id, added_by, added_at, updated_at)
            VALUES ({pending}, {Student}, 'pending', '待审', 'pending', '待审', 2, 'submission-1', 1, now(), now())
            """, Ct);
        await AssertFamilyViolationAsync(db.Database.ExecuteSqlAsync(Source(pending, "kind"), Ct));
        // Removing the last source of a family-only item is refused; removing the item removes its sources.
        var family = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await db.Database.ExecuteSqlAsync(ReadyItem(family, "kindly", familyOnly: true), Ct);
            await db.Database.ExecuteSqlAsync(Source(family, "kind"), Ct);
            await transaction.CommitAsync(Ct);
        }
        await AssertFamilyViolationAsync(db.Database.ExecuteSqlAsync(
            $"DELETE FROM vocab_notebook_family_source WHERE notebook_item_id = {family}", Ct));
        await db.Database.ExecuteSqlAsync($"DELETE FROM vocab_notebook_item WHERE id = {family}", Ct);

        // The repository inserts a family-only item before its sources in one save.
        var notebook = new VocabNotebookRepository(db);
        var added = new VocabNotebookItemModel
        {
            Id = Guid.NewGuid(),
            StudentId = Student,
            WordKey = "happiness",
            MeaningKey = "幸福",
            Word = "happiness",
            Meaning = "幸福",
            Status = VocabNotebookItemStatus.Ready,
            BookId = "book",
            WordId = "happiness-w",
            MeaningId = "happiness-m",
            FamilyOnly = true,
            AddedBy = VocabNotebookActorType.Student,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        added.AttachFamily(new("happy", "happy", "happy-w", "derived_noun", "outgoing"));
        await notebook.AddAsync(added, Ct);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        Assert.Equal("happy", Assert.Single((await notebook.FindAsync(Student, added.Id, Ct))!.FamilySources).Word);
    }

    [Fact]
    public async Task ExtraBatchLookupMatchesStructureRatherThanStoredText()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "storage-cleanup-extra";
        await storage.EnsureAsync(caller, Ct);
        await using var db = new LearningDbContext(storage.Options(caller));
        var plan = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity(id) VALUES ({Student})", Ct);
        const string segments = """[{"bookId":"book","unitIds":null}]""";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_plan(id, student_id, segments, daily_new_words, controller, status, start_date, total_words, version, created_at, updated_at)
            VALUES ({plan}, {Student}, {segments}, 10, 1, 1, '2026-11-01', 0, 1, now(), now())
            """, Ct);
        Guid open = Guid.NewGuid(), openRequest = Guid.NewGuid(), openQuestion = Guid.NewGuid(), done = Guid.NewGuid();
        // Spacing, line breaks and key order differ from what the serializer writes.
        await AddDayAsync(db, plan, new DateOnly(2026, 11, 2), $$"""
            { "QuestionIds": [], "ExtraBatches": [ { "CompletedAt": "2026-11-02T10:00:00+00:00", "Id": "{{done}}",
                "RequestIds": [], "QuestionIds": [] } ] }
            """);
        await AddDayAsync(db, plan, new DateOnly(2026, 11, 3), $$"""
            {
              "ExtraBatches": [
                { "QuestionIds": [ "{{openQuestion}}" ], "RequestIds": [ "{{openRequest}}" ], "Id": "{{open}}", "CompletedAt": null }
              ]
            }
            """);
        await AddDayAsync(db, plan, new DateOnly(2026, 11, 4), """{ "ExtraBatches": [] }""");

        var priorReview = new DateOnly(2026, 11, 5);
        var nextReview = priorReview.AddDays(1);
        await AddDayAsync(db, plan, priorReview, $$"""
            { "ExtraReviewBatches": [{ "Id": "{{open}}", "RequestIds": ["{{openRequest}}"], "QuestionIds": ["{{openQuestion}}"] }] }
            """);
        await AddDayAsync(db, plan, nextReview, $$"""
            { "ExtraReviewBatches": [{ "Id": "{{openQuestion}}", "QuestionIds": ["{{openRequest}}"] }] }
            """);
        var days = new VocabPlanDayRepository(db);
        Assert.Equal(priorReview, (await days.FindReviewQuestionDayAsync(plan, openQuestion, Ct))?.LocalDate);
        Assert.Equal(nextReview, (await days.FindReviewBatchDayAsync(plan, openQuestion, Ct))?.LocalDate);
        Assert.Equal(priorReview, (await days.FindReviewBatchDayAsync(plan, openRequest, Ct))?.LocalDate);
        Assert.Null(await days.FindReviewQuestionDayAsync(plan, open, Ct));
        foreach (var id in new Guid?[] { null, open, openRequest, openQuestion })
            Assert.Equal(new DateOnly(2026, 11, 3), (await days.FindExtraBatchDayAsync(plan, id, Ct))?.LocalDate);
        Assert.Equal(new DateOnly(2026, 11, 2), (await days.FindExtraBatchDayAsync(plan, done, Ct))?.LocalDate);
        Assert.Null(await days.FindExtraBatchDayAsync(plan, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task AnAbandonedRequestDoesNotRollTheUpgradeBack()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var storage = new LearningStorage(database.ConnectionString);
        const string caller = "storage-cleanup-abandoned";
        await using var db = await VersionFourAsync(storage, caller);
        using var abandoned = new CancellationTokenSource();
        await abandoned.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.EnsureAsync(caller, abandoned.Token));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await db.Database.SqlQuery<int>($"SELECT version AS \"Value\" FROM learning_schema_version").SingleAsync(Ct) != 6)
        {
            Assert.True(DateTime.UtcNow < deadline, "The upgrade did not finish after its request was abandoned.");
            await Task.Delay(100, Ct);
        }
        await storage.EnsureAsync(caller, Ct);
    }

    private static async Task<LearningDbContext> VersionFourAsync(LearningStorage storage, string caller)
    {
        var db = new LearningDbContext(storage.Options(caller));
        var createSchema = "CREATE SCHEMA " + LearningStorage.Schema(caller);
        await db.Database.ExecuteSqlRawAsync(createSchema, Ct);
        foreach (var file in new[] { "SchemaV1.sql", "ArchiveV2.sql", "UnitPracticeV3.sql", "UnitPracticeModesV4.sql" })
        {
            using var stream = typeof(LearningStorage).Assembly.GetManifestResourceStream("Lexarbor.Database.Learning." + file)!;
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(Ct), Ct);
            if (file == "SchemaV1.sql")
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE learning_schema_version (version integer PRIMARY KEY, store_id uuid NOT NULL DEFAULT gen_random_uuid(), generation uuid); INSERT INTO learning_schema_version (version) VALUES (1)", Ct);
        }
        return db;
    }

    private static async Task SeedLegacyRowsAsync(LearningDbContext db)
    {
        await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity(id) VALUES ({Student})", Ct);
        // Only one plan per learner may be active (status 1); the others are completed (status 2).
        await AddPlanAsync(db, LegacyUnits, 1, "legacy-book", """["u1","u2"]""", "[]");
        await AddPlanAsync(db, LegacyBook, 2, "legacy-book", null, "[]");
        await AddPlanAsync(db, LegacyEmptyUnits, 2, "legacy-book", "[]", "");
        await AddPlanAsync(db, MultiBook, 2, "a", null, """[{"bookId":"a","unitIds":null},{"bookId":"b","unitIds":["x"]}]""");
        await AddPlanAsync(db, ReviewOnly, 2, "", null, "[]");
        await AddPlanAsync(db, BlankSegments, 2, "", null, "");

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_notebook_item(id, student_id, word_key, meaning_key, word, meaning, status, book_id, word_id, meaning_id,
                added_by, added_at, updated_at, family_source_word_key, family_source_word, family_source_word_id,
                family_relation_type, family_direction, family_only)
            VALUES ({WordLevel}, {Student}, 'happiness', '幸福', 'happiness', '幸福', 1, 'book', 'happiness-w', 'happiness-m',
                1, now(), now(), 'happy', 'happy', 'happy-w', 'derived_noun', 'outgoing', true)
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_notebook_item(id, student_id, word_key, meaning_key, word, meaning, status, book_id, word_id, meaning_id,
                added_by, added_at, updated_at, family_source_word_key, family_source_word, family_source_word_id,
                family_relation_type, family_direction, family_relation_id, family_source_meaning_id, family_source_book_id)
            VALUES ({MeaningPair}, {Student}, 'quickly', '快速地', 'quickly', '快速地', 1, 'book', 'quickly-w', 'quickly-m',
                1, now(), now(), 'quick', 'quick', 'quick-w', 'derived_adverb', 'incoming', 'rel-1', 'quick-m', 'book')
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_notebook_item(id, student_id, word_key, meaning_key, word, meaning, status, book_id, word_id, meaning_id,
                added_by, added_at, updated_at, family_source_word_key, family_source_word, family_source_word_id,
                family_relation_type, family_direction)
            VALUES ({AlreadyMoved}, {Student}, 'darkness', '黑暗', 'darkness', '黑暗', 1, 'book', 'darkness-w', 'darkness-m',
                1, now(), now(), 'dark', 'column-source', 'dark-w', 'derived_noun', 'outgoing')
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_notebook_family_source(id, notebook_item_id, source_word_key, source_word, source_word_id, relation_type,
                direction, source_meaning_id, source_book_id, target_word_id, target_meaning_id, target_book_id)
            VALUES ({Guid.NewGuid()}, {AlreadyMoved}, 'dark', 'table-source', 'dark-w', 'derived_noun', 'outgoing', '', '',
                'darkness-w', 'darkness-m', 'book')
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_notebook_item(id, student_id, word_key, meaning_key, word, meaning, status, book_id, word_id, meaning_id,
                added_by, added_at, updated_at)
            VALUES ({Plain}, {Student}, 'pear', '梨', 'pear', '梨', 1, 'book', 'pear-w', 'pear-m', 1, now(), now())
            """, Ct);
    }

    private static Task<int> AddPlanAsync(LearningDbContext db, Guid id, int status, string bookId, string? unitIds, string segments) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_plan(id, student_id, book_id, unit_ids, segments, daily_new_words, controller, status, start_date,
                total_words, version, created_at, updated_at)
            VALUES ({id}, {Student}, {bookId}, {unitIds}, {segments}, 10, 1, {status}, '2026-01-01', 0, 1, now(), now())
            """, Ct);

    private static Task<int> AddDayAsync(LearningDbContext db, Guid plan, DateOnly date, string tasks) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_plan_day(plan_id, local_date, new_words_target, new_words_learned, due_in_scope, reviewed_in_scope,
                version, created_at, updated_at, daily_tasks)
            VALUES ({plan}, {date}, 10, 0, '[]', '[]', 1, now(), now(), {tasks})
            """, Ct);

    private static FormattableString ReadyItem(Guid id, string word, bool familyOnly) => $"""
        INSERT INTO vocab_notebook_item(id, student_id, word_key, meaning_key, word, meaning, status, book_id, word_id, meaning_id,
            added_by, added_at, updated_at, family_only)
        VALUES ({id}, {Student}, {word}, {word + "-m"}, {word}, {word + "-m"}, 1, 'book', {word + "-w"}, {word + "-m"}, 1, now(), now(), {familyOnly})
        """;

    private static FormattableString Source(Guid item, string sourceWord) => $"""
        INSERT INTO vocab_notebook_family_source(id, notebook_item_id, source_word_key, source_word, source_word_id, relation_type,
            direction, source_meaning_id, source_book_id, target_word_id, target_meaning_id, target_book_id)
        VALUES ({Guid.NewGuid()}, {item}, {sourceWord}, {sourceWord}, {sourceWord + "-w"}, 'derived_adverb', 'outgoing', '', '',
            'target-w', 'target-m', 'book')
        """;

    private static async Task AssertFamilyViolationAsync(Task write)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => write);
        Assert.Equal((PostgresErrorCodes.CheckViolation, "CK_vocab_notebook_item_family"), (error.SqlState, error.ConstraintName));
    }

    private static string Scope(VocabPlanModel? plan) => string.Join(" ", plan!.Segments.Select(segment =>
        $"{segment.BookId}[{(segment.UnitIds is null ? "*" : string.Join(",", segment.UnitIds))}]"));
}
