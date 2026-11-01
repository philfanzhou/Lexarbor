using System.Text.Json;
using Lexarbor.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class MeaningSubmissionMigrationTests : IDisposable
{
    // The last migration shipped in v0.1.6.
    private const string PreviousMigration = "20261021010701_RemoveTextbookAudio";
    private const string Migration = "20261031172216_AddMeaningSubmissions";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lexarbor-submission-migration-{Guid.NewGuid():N}");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpgradeAndDown_OnlyAddAndDropSubmissionTable_KeepAllVocabularyRowsAndSchema()
    {
        Directory.CreateDirectory(_directory);
        await using var context = Context();
        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO vocabulary_book (id, book_name, display_order, status, created_at, updated_at) VALUES ('book', 'Retained book', 0, 1, '2026-01-01', '2026-01-01');
            INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('word', 'Retained Word', '2026-01-01', '2026-01-01');
            INSERT INTO vocabulary_meaning (id, vocabulary_id, book_id, part_of_speech, meaning, created_at, updated_at)
                VALUES ('meaning', 'word', 'book', 'n.', 'retained meaning', '2026-01-01', '2026-01-01');
            INSERT INTO vocabulary_book_unit (id, book_id, number, created_at, updated_at)
                VALUES ('unit', 'book', 1, '2026-01-01', '2026-01-01');
            INSERT INTO vocabulary_meaning_unit VALUES ('unit', 'meaning', 'book', 'A', 'phrase');
            """, Ct);
        var before = await VocabularySnapshot(context);

        await context.GetService<IMigrator>().MigrateAsync(Migration, Ct);

        Assert.Equal(before, await VocabularySnapshot(context));
        Assert.Empty(await context.MeaningSubmissions.ToListAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
        await Insert(context, "pending", "  Ruoyu ", "N.", " a wise fool ", 0);
        Assert.Equal("""[["ruoyu","n.","a wise fool"]]""", await Rows(context, "SELECT normalized_word, normalized_part_of_speech, normalized_meaning FROM meaning_submission"));
        // Only one pending submission per key; reviewed ones may repeat it.
        await Assert.ThrowsAsync<SqliteException>(() => Insert(context, "duplicate", "ruoyu", "n.", "a wise fool", 0));
        await Insert(context, "approved", "ruoyu", "n.", "a wise fool", 1);
        Assert.Contains("IX_meaning_submission_pending_key", await Rows(context, "PRAGMA index_list(meaning_submission)"));

        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        Assert.Equal(before, await VocabularySnapshot(context));
        Assert.Equal("[]", await Rows(context, "SELECT name FROM sqlite_master WHERE name='meaning_submission'"));
    }

    private Task<int> Insert(VocabularyDbContext context, string id, string word, string partOfSpeech, string meaning, int status) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO meaning_submission (id, source, word, part_of_speech, meaning, status, created_at_unix_ms, updated_at_unix_ms) VALUES ({0}, 'student', {1}, {2}, {3}, {4}, 1, 1)",
            [id, word, partOfSpeech, meaning, status], Ct);

    private async Task<string> VocabularySnapshot(VocabularyDbContext context)
    {
        // Every table the previous release had, so nothing else may change.
        var tables = JsonSerializer.Deserialize<string[][]>(await Rows(context,
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('meaning_submission', '__EFMigrationsHistory') AND name NOT LIKE 'sqlite_%' ORDER BY name"))!;
        Assert.Contains(tables, row => row[0] == "vocabulary_meaning");
        var result = new List<string>();
        foreach (var table in tables.Select(row => row[0]))
        {
            result.Add(await Rows(context, $"SELECT * FROM {table} ORDER BY 1, 2"));
            result.Add(await Rows(context, $"SELECT type, name, sql FROM sqlite_master WHERE tbl_name='{table}' ORDER BY type, name"));
        }
        return JsonSerializer.Serialize(result);
    }

    private async Task<string> Rows(VocabularyDbContext context, string sql)
    {
        await context.Database.OpenConnectionAsync(Ct);
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new List<string[]>();
        while (await reader.ReadAsync(Ct))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++) values[i] = reader.GetValue(i).ToString() ?? "";
            result.Add(values);
        }
        return JsonSerializer.Serialize(result);
    }

    private VocabularyDbContext Context() => new(new DbContextOptionsBuilder<VocabularyDbContext>()
        .UseSqlite($"Data Source={Path.Combine(_directory, "migration.db")};Pooling=False").Options);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
