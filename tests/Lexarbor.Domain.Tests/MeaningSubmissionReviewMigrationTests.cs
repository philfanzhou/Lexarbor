using System.Text.Json;
using Lexarbor.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class MeaningSubmissionReviewMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task UpDown_PreserveEveryOldTableAndSubmissionValue_KeepPendingUniqueIndex()
    {
        var root = Directory.CreateTempSubdirectory("lexarbor-review-migration-").FullName;
        try
        {
            await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={Path.Combine(root, "migration.db")};Pooling=False").Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261101213624_AddEnrichmentScheduler", Ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO vocabulary_book (id,book_name,display_order,status,created_at,updated_at) VALUES ('book','Retained',0,1,'2026-01-01','2026-01-01');
                INSERT INTO vocabulary (id,word,created_at,updated_at) VALUES ('word','Word','2026-01-01','2026-01-01');
                INSERT INTO vocabulary_meaning (id,vocabulary_id,book_id,meaning,created_at,updated_at) VALUES ('meaning','word','book','retained','2026-01-01','2026-01-01');
                INSERT INTO meaning_submission (id,source,word,part_of_speech,meaning,example,status,result_book_id,result_word_id,result_meaning_id,result_word,result_meaning,result_part_of_speech,created_at_unix_ms,updated_at_unix_ms,reviewed_at_unix_ms) VALUES
                ('pending','source',' Word ','N.',' retained ','example',0,NULL,NULL,NULL,NULL,NULL,NULL,1,2,NULL),
                ('approved','source','Word','n.','retained','example',1,'book','word','meaning','Word','retained','n.',1,2,2),
                ('rejected','source','Word','n.','retained','example',2,NULL,NULL,NULL,NULL,NULL,NULL,1,2,2);
                """, Ct);
            var oldColumns = await Rows(db, "SELECT name FROM pragma_table_xinfo('meaning_submission') ORDER BY name");
            var columns = "id,source,word,normalized_word,part_of_speech,normalized_part_of_speech,meaning,normalized_meaning,example,status,result_book_id,result_word_id,result_meaning_id,result_word,result_meaning,result_part_of_speech,created_at_unix_ms,updated_at_unix_ms,reviewed_at_unix_ms";
            var before = await Rows(db, $"SELECT {columns} FROM meaning_submission ORDER BY id");
            var otherBefore = await OtherTables(db);
            await migrator.MigrateAsync(cancellationToken: Ct);
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(before, await Rows(db, $"SELECT {columns} FROM meaning_submission ORDER BY id"));
            Assert.Equal(otherBefore, await OtherTables(db));
            Assert.Equal("[[\"3\"]]", await Rows(db, "SELECT COUNT(*) FROM meaning_submission WHERE review_comment IS NULL AND reviewer_kind IS NULL AND ai_review_verdict IS NULL"));
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE meaning_submission SET review_comment='new reason',reviewer_kind='ai',ai_review_verdict='reject' WHERE id='rejected';
                INSERT INTO meaning_submission_ai_review (id,attempt_id,submission_id,verdict,model,reviewed_at_unix_ms,applied) VALUES ('record','attempt','rejected','reject','fixture',3,0);
                """, Ct);
            await migrator.MigrateAsync("20261101213624_AddEnrichmentScheduler", Ct);
            Assert.Equal(before, await Rows(db, $"SELECT {columns} FROM meaning_submission ORDER BY id"));
            Assert.Equal(oldColumns, await Rows(db, "SELECT name FROM pragma_table_xinfo('meaning_submission') ORDER BY name"));
            Assert.Equal(otherBefore, await OtherTables(db));
            Assert.Equal("[]", await Rows(db, "SELECT name FROM sqlite_master WHERE name='meaning_submission_ai_review'"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("""
                INSERT INTO meaning_submission (id,source,word,part_of_speech,meaning,status,created_at_unix_ms,updated_at_unix_ms) VALUES ('duplicate','source','word','n.','retained',0,1,1)
                """, Ct));
            Assert.Contains("IX_meaning_submission_pending_key", await Rows(db, "PRAGMA index_list(meaning_submission)"));
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<string> OtherTables(VocabularyDbContext db)
    {
        var names = JsonSerializer.Deserialize<string[][]>(await Rows(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('meaning_submission','meaning_submission_ai_review','__EFMigrationsHistory') AND name NOT LIKE 'sqlite_%' ORDER BY name"))!;
        var snapshot = new List<string>();
        foreach (var table in names.Select(r => r[0]))
        {
            snapshot.Add(await Rows(db, $"SELECT * FROM {table} ORDER BY 1"));
            snapshot.Add(await Rows(db, $"SELECT type,name,sql FROM sqlite_master WHERE tbl_name='{table}' ORDER BY type,name"));
        }
        return JsonSerializer.Serialize(snapshot);
    }
    private static async Task<string> Rows(VocabularyDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync(Ct);
        using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(Ct); var rows = new List<string[]>();
        while (await reader.ReadAsync(Ct)) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString() ?? "").ToArray());
        return JsonSerializer.Serialize(rows);
    }
}
