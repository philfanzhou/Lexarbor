using System.Text.Json;
using Lexarbor.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class VocabularyRelationMigrationTests
{
    private const string Previous = "20261102063605_AddMeaningSubmissionReviewChecks";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpgradeAndDownPreserveCatalog_AndMigratedSchemaEnforcesRelationIntegrity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(Ct);
        await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous, Ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO vocabulary(id, word, created_at, updated_at) VALUES ('cut', 'cut', '2026-01-01', '2026-01-01');
            INSERT INTO vocabulary(id, word, created_at, updated_at) VALUES ('write', 'write', '2026-01-01', '2026-01-01');
            INSERT INTO vocabulary_book(id, book_name, display_order, status, created_at, updated_at) VALUES ('book','Book',0,1,'2026-01-01','2026-01-01');
            INSERT INTO vocabulary_meaning(id, vocabulary_id, book_id, meaning, created_at, updated_at) VALUES ('m','write','book','write','2026-01-01','2026-01-01');
            """, Ct);
        var words = await Rows(connection, "SELECT * FROM vocabulary ORDER BY id");
        var meanings = await Rows(connection, "SELECT * FROM vocabulary_meaning");
        var schema = await Rows(connection, "SELECT type,name,sql FROM sqlite_master WHERE tbl_name NOT IN ('vocabulary_relation','__EFMigrationsHistory') ORDER BY name");
        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.VocabularyRelations.ToListAsync(Ct));
        Assert.Equal(words, await Rows(connection, "SELECT * FROM vocabulary ORDER BY id"));
        Assert.Equal(meanings, await Rows(connection, "SELECT * FROM vocabulary_meaning"));
        Assert.Equal(schema, await Rows(connection, "SELECT type,name,sql FROM sqlite_master WHERE tbl_name NOT IN ('vocabulary_relation','__EFMigrationsHistory') ORDER BY name"));
        var columns = JsonSerializer.Deserialize<string[][]>(await Rows(connection, "PRAGMA table_info(vocabulary_relation)"))!;
        Assert.Equal(13, columns.Length);
        Assert.DoesNotContain(columns, c => c[1].Contains("part_of_speech", StringComparison.Ordinal));
        foreach (var type in Lexarbor.Domain.Models.VocabularyRelationTypes.All)
            await Insert(db, type.Code, type.Code);
        Assert.Equal(11, await db.VocabularyRelations.CountAsync(Ct));
        var defaults = await db.VocabularyRelations.AsNoTracking().FirstAsync(Ct);
        Assert.Equal(("needs_review", "new_relation", 1L), (defaults.Status, defaults.ReviewReason, defaults.Version));
        Assert.Null(defaults.LastConfirmedAtUnixMs);
        await Assert.ThrowsAsync<SqliteException>(() => Insert(db, "duplicate", "past_tense"));
        await Assert.ThrowsAsync<SqliteException>(() => Insert(db, "unknown", "base_form"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='cut'", Ct));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET id='changed' WHERE id='cut'", Ct));
        foreach (var assignment in new[] { "source_word_id='missing'", "target_word_id='missing'", "status='confirmed'",
                     "review_reason=NULL", "version=0", "version=1.5", "updated_at_unix_ms=-1", "usage_note=' '", "origin_kind='unknown'" })
        {
            // Assignment fragments above are test constants, never external inputs.
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE vocabulary_relation SET {assignment} WHERE id='past_tense'";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync(Ct));
        }
        var foreignKeys = JsonSerializer.Deserialize<string[][]>(await Rows(connection, "PRAGMA foreign_key_list(vocabulary_relation)"))!;
        Assert.Equal(2, foreignKeys.Length);
        Assert.All(foreignKeys, key => { Assert.Equal("RESTRICT", key[5]); Assert.Equal("RESTRICT", key[6]); });
        Assert.Equal("[]", await Rows(connection, "PRAGMA foreign_key_check"));
        await migrator.MigrateAsync(Previous, Ct);
        Assert.Equal("[]", await Rows(connection, "SELECT name FROM sqlite_master WHERE tbl_name='vocabulary_relation'"));
        Assert.Equal(words, await Rows(connection, "SELECT * FROM vocabulary ORDER BY id"));
        Assert.Equal(meanings, await Rows(connection, "SELECT * FROM vocabulary_meaning"));
        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Empty(await db.VocabularyRelations.ToListAsync(Ct));
    }

    private Task<int> Insert(VocabularyDbContext db, string id, string type) => db.Database.ExecuteSqlRawAsync(
        "INSERT INTO vocabulary_relation(id,source_word_id,target_word_id,type,origin_kind,created_at_unix_ms,updated_at_unix_ms) VALUES ({0},'cut','cut',{1},'manual',1,1)", [id, type], Ct);

    private async Task<string> Rows(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(Ct))
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString() ?? "").ToArray());
        return JsonSerializer.Serialize(rows);
    }
}
