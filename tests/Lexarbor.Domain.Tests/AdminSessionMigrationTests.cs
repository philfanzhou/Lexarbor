using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AdminSessionMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260928131352_AddMeaningUnitEntryKinds";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lexarbor-session-migration-{Guid.NewGuid():N}");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpgradeAndDown_OnlyAddAndDropSessionTable_KeepAllVocabularyRowsAndSchema()
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
        await context.Database.MigrateAsync(Ct);
        Assert.Equal(before, await VocabularySnapshot(context));
        Assert.Empty(await context.AdminSessions.ToListAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
        var columns = await Rows(context, "PRAGMA table_info(admin_session)");
        Assert.Contains("expires_at_unix_ms", columns);
        Assert.Contains("INTEGER", columns);
        Assert.Contains("protected_payload", columns);
        var plan = await Rows(context, "EXPLAIN QUERY PLAN SELECT handle_hash FROM admin_session WHERE expires_at_unix_ms <= 0 ORDER BY expires_at_unix_ms, handle_hash LIMIT 100");
        Assert.Contains("IX_admin_session_expires_at_unix_ms_handle_hash", plan);
        context.AdminSessions.Add(new AdminSessionEntity { HandleHash = "synthetic-hash", ProtectedPayload = "synthetic-ciphertext", ExpiresAtUnixMs = 123 });
        await context.SaveChangesAsync(Ct);
        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        Assert.Equal(before, await VocabularySnapshot(context));
        Assert.Equal("[]", await Rows(context, "SELECT name FROM sqlite_master WHERE name='admin_session'"));
    }

    [Fact]
    public async Task FreshDatabase_IsEmptyAndHasExpiryIndexAndCurrentModel()
    {
        Directory.CreateDirectory(_directory);
        await using var context = Context();
        await context.Database.MigrateAsync(Ct);
        Assert.Empty(await context.AdminSessions.ToListAsync(Ct));
        Assert.Empty(await context.Vocabularies.ToListAsync(Ct));
        Assert.Empty(await context.VocabularyBooks.ToListAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains("IX_admin_session_expires_at_unix_ms_handle_hash", await Rows(context, "PRAGMA index_list(admin_session)"));
    }

    private async Task<string> VocabularySnapshot(VocabularyDbContext context)
    {
        var result = new List<string>();
        foreach (var table in new[] { "vocabulary", "vocabulary_book", "vocabulary_meaning", "vocabulary_book_unit", "vocabulary_meaning_unit" })
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
