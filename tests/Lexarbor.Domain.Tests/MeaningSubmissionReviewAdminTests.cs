using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class MeaningSubmissionReviewAdminTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lexarbor-review-admin-").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private VocabularyDbContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={Path.Combine(_root, "fixture.db")};Pooling=False").AddInterceptors(interceptors).Options);
    private static MeaningSubmissionReviewAdminRepository Repo(VocabularyDbContext db) => new(db, TimeProvider.System);
    private static async Task Seed(VocabularyDbContext db)
    {
        await db.Database.MigrateAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO vocabulary_book (id,book_name,display_order,status,created_at,updated_at) VALUES ('book','Fixture',0,1,'2026-01-01','2026-01-01');
            INSERT INTO vocabulary (id,word,created_at,updated_at) VALUES ('word','Word','2026-01-01','2026-01-01');
            INSERT INTO vocabulary_meaning (id,vocabulary_id,book_id,meaning,created_at,updated_at) VALUES ('meaning','word','book','current catalog','2026-01-01','2026-01-01');
            INSERT INTO meaning_submission (id,source,word,part_of_speech,meaning,example,status,result_word_id,created_at_unix_ms,updated_at_unix_ms,reviewer_kind) VALUES ('submission','client','Word','n.','original meaning','original example',1,'word',1,2,'ai');
            INSERT INTO meaning_submission_ai_review (id,attempt_id,submission_id,verdict,part_of_speech,meaning,phonetic_uk,phonetic_us,comment,model,reviewed_at_unix_ms,applied) VALUES
            ('a','attempt-a','submission','accept','n.','original meaning','/uk/','/us/','accept note','model-a',5,1),
            ('b','attempt-b','submission','revise','v.','original meaning',NULL,NULL,'POS only','model-b',5,1),
            ('c','attempt-c','submission','revise','n.','original meaning','/ipa-only/',NULL,'IPA only','model-c',5,1),
            ('d','attempt-d','submission','reject',NULL,NULL,NULL,NULL,'Spelling: word','model-d',4,1),
            ('e','attempt-e','submission','revise','v.','late meaning','/late/','/late/','Lost race','old-captured-model',3,0);
            """, Ct);
    }

    [Fact]
    public async Task Filters_StablePages_ExactOriginalAndAiProjection_ReadsNeverWrite()
    {
        await using var db = Open(); await Seed(db);
        var before = await Snapshot(db);
        var repository = Repo(db);
        var (first, total) = await repository.PageAsync(new(Size: 2), Ct);
        Assert.Equal(5, total); Assert.Equal(new[] { "a", "b" }, first.Select(r => r.RecordId));
        Assert.Equal(new[] { "c", "d" }, (await repository.PageAsync(new(Page: 2, Size: 2), Ct)).Items.Select(r => r.RecordId));
        Assert.Equal("e", Assert.Single((await repository.PageAsync(new(Page: 3, Size: 2), Ct)).Items).RecordId);
        Assert.Empty((await repository.PageAsync(new(Page: 4, Size: 2), Ct)).Items);
        foreach (var verdict in new[] { "accept", "revise", "reject" })
            Assert.All((await repository.PageAsync(new(Verdict: verdict), Ct)).Items, row => Assert.Equal(verdict, row.Verdict));
        var revisions = (await repository.PageAsync(new(Verdict: "revise", Applied: true), Ct)).Items;
        Assert.Equal(new[] { "b", "c" }, revisions.Select(row => row.RecordId));
        Assert.Equal("v.", revisions[0].PartOfSpeech); Assert.Null(revisions[0].PhoneticUk);
        Assert.Equal("n.", revisions[1].PartOfSpeech); Assert.Equal("/ipa-only/", revisions[1].PhoneticUk);
        var late = Assert.Single((await repository.PageAsync(new(Applied: false), Ct)).Items);
        Assert.False(late.Applied); Assert.Null(late.ResultWordId); Assert.Equal("old-captured-model", late.Model);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(3), late.ReviewedAt);
        Assert.Equal("Lost race", late.Comment); Assert.Equal("late meaning", late.Meaning); Assert.Equal("/late/", late.PhoneticUs);
        Assert.Equal("client", late.Source); Assert.Equal("Word", late.Word); Assert.Equal("n.", late.OriginalPartOfSpeech);
        Assert.Equal("original meaning", late.OriginalMeaning); Assert.Equal("original example", late.OriginalExample);
        Assert.Equal("e", Assert.Single((await repository.PageAsync(new(RecordId: "e"), Ct)).Items).RecordId);
        Assert.Empty((await repository.PageAsync(new(Checked: true), Ct)).Items);
        Assert.Equal(5, (await repository.PageAsync(new(Checked: false), Ct)).TotalCount);
        Assert.Equal(before, await Snapshot(db));
    }

    [Fact]
    public async Task ConcurrentChecks_KeepFirstFact_PerRecord_AndHistoryAfterCatalogChangesAndRestart()
    {
        await using (var db = Open()) { await Seed(db); }
        var checks = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        { await using var db = Open(); return await Repo(db).CheckAsync("b", Ct); }));
        Assert.All(checks, check => Assert.Equal(checks[0], check));
        await using (var db = Open())
        {
            Assert.Single(await db.MeaningSubmissionReviewChecks.ToListAsync(Ct));
            Assert.Equal(checks[0], await Repo(db).CheckAsync("b", Ct));
            Assert.Equal(1, (await Repo(db).PageAsync(new(Checked: true), Ct)).TotalCount);
            Assert.Equal(4, (await Repo(db).PageAsync(new(Checked: false), Ct)).TotalCount);
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => Repo(db).CheckAsync("missing", Ct));
            await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET meaning='later correction'; DELETE FROM vocabulary;", Ct);
            // A later valid conclusion for the same submission must not inherit the old record's check.
            await db.Database.ExecuteSqlRawAsync("INSERT INTO meaning_submission_ai_review(id,attempt_id,submission_id,verdict,model,reviewed_at_unix_ms,applied) VALUES ('later','later-attempt','submission','accept','later-model',9,0)", Ct);
        }
        await using var restarted = Open();
        var row = Assert.Single((await Repo(restarted).PageAsync(new(RecordId: "b"), Ct)).Items);
        Assert.Equal(checks[0].CheckedAt, row.CheckedAt); Assert.Equal("POS only", row.Comment);
        Assert.Equal("model-b", row.Model); Assert.Equal("original meaning", row.OriginalMeaning);
        Assert.Null(Assert.Single((await Repo(restarted).PageAsync(new(RecordId: "later"), Ct)).Items).CheckedAt);
        Assert.Equal(MeaningSubmissionStatus.Approved, (await restarted.MeaningSubmissions.SingleAsync(Ct)).Status);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => restarted.Database.ExecuteSqlRawAsync("UPDATE meaning_submission_ai_review SET comment='changed'", Ct));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => restarted.Database.ExecuteSqlRawAsync("DELETE FROM meaning_submission_ai_review", Ct));
    }

    [Fact]
    public async Task Cancellation_BeforeAndDuringCheck_RollsBackOnlyTheNewFact()
    {
        await using (var db = Open()) { await Seed(db); }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var db = Open(new CancelCheck(cancel)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repo(db).CheckAsync("a", cancel.Token));
        await using var reopened = Open(); Assert.Empty(await reopened.MeaningSubmissionReviewChecks.ToListAsync(Ct));
        Assert.Equal(5, await reopened.MeaningSubmissionAiReviews.CountAsync(Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repo(reopened).CheckAsync("a", cancel.Token));
        Assert.Equal("a", (await Repo(reopened).CheckAsync("a", Ct)).RecordId);
    }

    [Fact]
    public async Task CountAndPage_UseOneReadSnapshot_WhenAnotherWriterAppendsAfterCount()
    {
        await using (var db = Open()) { await Seed(db); }
        var intercept = new AppendBetweenCountAndPage(async () =>
        {
            await using var writer = Open();
            await writer.Database.ExecuteSqlRawAsync("INSERT INTO meaning_submission_ai_review(id,attempt_id,submission_id,verdict,model,reviewed_at_unix_ms,applied) VALUES ('new','new-attempt','submission','accept','new-model',99,0)", Ct);
        });
        await using var reader = Open(intercept);
        var page = await Repo(reader).PageAsync(new(), Ct);
        Assert.True(intercept.Appended); Assert.Equal(5, page.TotalCount); Assert.Equal(5, page.Items.Count);
        Assert.DoesNotContain(page.Items, row => row.RecordId == "new");
        await using var current = Open(); Assert.Equal(6, (await Repo(current).PageAsync(new(), Ct)).TotalCount);
    }

    [Fact]
    public async Task Migration_OnlyAddsEmptyCheckTable_DownPreservesAllPreviousDataAndConclusions()
    {
        await using var db = Open(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261102052837_AddAutomaticMeaningSubmissionReview", Ct);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO meaning_submission(id,source,word,meaning,status,created_at_unix_ms,updated_at_unix_ms) VALUES ('s','client','Word','original',0,1,2); INSERT INTO meaning_submission_ai_review(id,attempt_id,submission_id,verdict,model,reviewed_at_unix_ms,applied) VALUES ('r','attempt','s','reject','captured-model',2,0)", Ct);
        var before = await Snapshot(db);
        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(before, await Snapshot(db));
        Assert.Empty(await db.MeaningSubmissionReviewChecks.ToListAsync(Ct));
        await Repo(db).CheckAsync("r", Ct);
        await migrator.MigrateAsync("20261102052837_AddAutomaticMeaningSubmissionReview", Ct);
        Assert.Equal(before, await Snapshot(db));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM meaning_submission_ai_review", Ct));
    }

    private static async Task<string> Snapshot(VocabularyDbContext db)
    {
        await db.Database.OpenConnectionAsync(Ct);
        try
        {
            var connection = db.Database.GetDbConnection(); var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('meaning_submission_review_check','__EFMigrationsHistory') AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var names = await command.ExecuteReaderAsync(Ct); while (await names.ReadAsync(Ct)) tables.Add(names.GetString(0));
            }
            var snapshot = new List<object>();
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
                await using var rows = await command.ExecuteReaderAsync(Ct); var values = new List<string[]>();
                while (await rows.ReadAsync(Ct)) values.Add(Enumerable.Range(0, rows.FieldCount).Select(i => rows.GetValue(i).ToString() ?? "").ToArray());
                await rows.DisposeAsync();
                snapshot.Add(new { table, values });
                using var schema = connection.CreateCommand(); schema.CommandText = $"SELECT type,name,sql FROM sqlite_master WHERE tbl_name='{table}' ORDER BY type,name";
                using var definitions = await schema.ExecuteReaderAsync(Ct); var ddl = new List<string[]>();
                while (await definitions.ReadAsync(Ct)) ddl.Add(Enumerable.Range(0, definitions.FieldCount).Select(i => definitions.GetValue(i).ToString() ?? "").ToArray());
                snapshot.Add(ddl);
            }
            return JsonSerializer.Serialize(snapshot);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
    private sealed class CancelCheck(CancellationTokenSource cancel) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        { if (command.CommandText.StartsWith("INSERT INTO meaning_submission_review_check", StringComparison.Ordinal)) { Assert.Equal(1, result); cancel.Cancel(); } return ValueTask.FromResult(result); }
    }
    private sealed class AppendBetweenCountAndPage(Func<Task> append) : DbCommandInterceptor
    {
        public bool Appended { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (!Appended && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal)) { Appended = true; await append(); } return result; }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
