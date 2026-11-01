using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class EnrichmentMigrationTests
{
    private const string Previous = "20261101125550_AddMeaningPhonetics";
    private const string Migration = "20261101163649_AddEnrichmentTasks";
    private static CancellationToken Token => AudioTestDatabase.Token;

    [Fact]
    public async Task UpgradeDownAndFreshSchema_PreserveAllExistingRowsAndPermanentReceipts()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token); await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.Add(AudioTestDatabase.Asset("existing"));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("existing", AudioLocale.EnGb, null, AudioTestDatabase.Revision(AudioLocale.EnGb)));
        await db.SaveChangesAsync(Token); await AudioTestDatabase.SeedSucceededOperationAsync(db, "existing"); db.ChangeTracker.Clear(); var before = await OldRowsAsync(file);
        await db.GetService<IMigrator>().MigrateAsync(Migration, Token);
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(before, await OldRowsAsync(file));
        await db.Database.MigrateAsync(Token); // Current repositories require the current schema.
        var repo = new EnrichmentTaskRepository(db);
        var task = Assert.IsType<EnrichmentTask>(await repo.EnsureAsync(new("word", null, AudioLocale.EnGb, EnrichmentStep.Audio), "config", 1, Token));
        Assert.Single(task.Rounds); Assert.Empty(task.Calls);
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name LIKE 'enrichment_%'"));
        Assert.Equal(before, await OldRowsAsync(file));
        await db.GetService<IMigrator>().MigrateAsync(Migration, Token);
        Assert.Empty(await db.EnrichmentTargets.ToListAsync(Token)); Assert.Equal(before, await OldRowsAsync(file));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
        using var fresh = new AudioTestDatabase(); await using var freshDb = fresh.Open(); await freshDb.Database.MigrateAsync(Token);
        await db.Database.MigrateAsync(Token);
        const string schema = "SELECT type,name,sql FROM sqlite_master WHERE name LIKE '%enrichment%' ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(schema)), JsonSerializer.Serialize(await file.RowsAsync(schema)));
    }

    [Fact]
    public async Task SqliteIndexesAndChecks_RejectNullDuplicatesAndInvalidStatesWithoutTouchingOldData()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='specific' WHERE id='meaning'", Token);
        var repo = new EnrichmentTaskRepository(db);
        foreach (var meaning in new string?[] { null, "meaning" })
        {
            var task = Assert.IsType<EnrichmentTask>(await repo.EnsureAsync(new("word", meaning, AudioLocale.EnGb, EnrichmentStep.Audio), "config", 1, Token));
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO enrichment_target(id,word_id,meaning_id,locale,step,current_round,epoch,deleted)
                SELECT {Guid.NewGuid().ToString()},word_id,meaning_id,locale,step,1,1,0 FROM enrichment_target WHERE id={task.Id}
                """, Token));
            Assert.Equal(19, error.SqliteErrorCode);
        }
        foreach (var sql in new[] {
            "UPDATE enrichment_round SET consumed_failures=6",
            "UPDATE enrichment_round SET request_count=-1",
            "UPDATE enrichment_round SET state='Running'",
            "UPDATE enrichment_target SET locale='fr-FR'",
            "UPDATE enrichment_target SET step='Fields' WHERE meaning_id IS NOT NULL" })
            Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, Token))).SqliteErrorCode);
        Assert.Equal(2, await db.EnrichmentTargets.CountAsync(Token));
        Assert.Equal(2, await db.Vocabularies.CountAsync(Token)); Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task FailedMigration_RollsBackTablesTriggersAndHistoryAndCanResume()
    {
        using var file = new AudioTestDatabase();
        await using (var db = file.Open())
        { await db.GetService<IMigrator>().MigrateAsync(Previous, Token); await AudioTestDatabase.SeedAsync(db); }
        var before = await OldRowsAsync(file); var fault = new FailRoundCreation();
        await using (var db = file.Open(fault))
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.GetService<IMigrator>().MigrateAsync(Migration, Token));
        Assert.True(fault.Triggered); Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name LIKE '%enrichment%'"));
        Assert.Empty(await file.RowsAsync($"SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId='{Migration}'"));
        Assert.Equal(before, await OldRowsAsync(file));
        await using var resumed = file.Open(); await resumed.GetService<IMigrator>().MigrateAsync(Migration, Token);
        Assert.Equal(before, await OldRowsAsync(file));
    }

    private static async Task<string> OldRowsAsync(AudioTestDatabase file)
    {
        var rows = new List<object?>();
        var tables = await file.RowsAsync("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'enrichment_%' AND name NOT IN ('__EFMigrationsHistory','audio_invalidation','meaning_phonetic_epoch') ORDER BY name");
        foreach (var item in tables)
        {
            var values = Assert.IsType<object?[]>(item);
            var table = Assert.IsType<string>(values[0]);
            var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            rows.Add(await file.RowsAsync($"SELECT {await file.PreEpochColumnsAsync(table)} FROM {quoted} ORDER BY 1"));
        }
        return JsonSerializer.Serialize(rows);
    }
    private sealed class FailRoundCreation : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("CREATE TABLE \"enrichment_round\"", StringComparison.Ordinal))
            { Triggered = true; throw new InvalidOperationException("Injected migration failure."); }
            return ValueTask.FromResult(result);
        }
    }
}
