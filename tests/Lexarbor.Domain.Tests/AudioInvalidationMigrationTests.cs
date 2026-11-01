using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioInvalidationMigrationTests
{
    private const string Previous = "20261101163649_AddEnrichmentTasks";
    private static CancellationToken Token => AudioTestDatabase.Token;

    [Fact]
    public async Task UpgradeAndDown_PreserveAllOldRowsAndReceipt_AndDoNotRetroactivelyAuthorize()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token); await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.Add(AudioTestDatabase.Asset("published"));
        db.AudioUploadOperations.Add(AudioTestDatabase.Operation("published", AudioUploadState.Succeeded));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("published", AudioLocale.EnGb, "meaning", AudioTestDatabase.Hash));
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear(); var before = await Capture(file);
        await db.Database.MigrateAsync(Token);
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(before, await Capture(file));
        Assert.Empty(await db.AudioInvalidations.ToListAsync(Token));
        using var fresh = new AudioTestDatabase(); await using var freshDb = fresh.Open(); await freshDb.Database.MigrateAsync(Token);
        const string schema = "SELECT type,name,sql FROM sqlite_master WHERE name LIKE '%invalidation%' ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(schema)), JsonSerializer.Serialize(await file.RowsAsync(schema)));
        var repo = new AudioInvalidationRepository(db);
        await new UnitOfWork(db).ExecuteInTransactionAsync(() => repo.AuthorizeInTransactionAsync(new("word", "meaning", AudioLocale.EnGb, "published", 9), 10, Token));
        Assert.True(await new AudioRecordExistenceRepository(db).HasAnyAsync(Token));
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name LIKE '%invalidation%'"));
        Assert.Equal(before, await Capture(file));
        await db.Database.MigrateAsync(Token); Assert.Empty(await db.AudioInvalidations.ToListAsync(Token));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task FailedMigration_RollsBackSchemaAndHistory_AndCanResume()
    {
        using var file = new AudioTestDatabase();
        await using (var db = file.Open()) { await db.GetService<IMigrator>().MigrateAsync(Previous, Token); await AudioTestDatabase.SeedAsync(db); }
        var before = await Capture(file); var fault = new FailTrigger();
        await using (var failing = file.Open(fault)) await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Database.MigrateAsync(Token));
        Assert.True(fault.Triggered); Assert.Equal(before, await Capture(file));
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name LIKE '%invalidation%'"));
        Assert.Empty(await file.RowsAsync("SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId LIKE '%AddAudioInvalidations'"));
        await using var resumed = file.Open(); await resumed.Database.MigrateAsync(Token);
        Assert.Equal(before, await Capture(file)); Assert.False(resumed.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task DestinationGuardIncludesAuthorityEvenIfExternalWriterBypassesForeignKey()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.Add(AudioTestDatabase.Asset("published"));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("published", AudioLocale.EnGb, "meaning", AudioTestDatabase.Hash));
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        await new UnitOfWork(db).ExecuteInTransactionAsync(() => new AudioInvalidationRepository(db).AuthorizeInTransactionAsync(new("word", "meaning", AudioLocale.EnGb, "published", 1), 10, Token));
        await db.Database.OpenConnectionAsync(Token); await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF", Token);
        await db.VocabularyAudioBindings.ExecuteDeleteAsync(Token); await db.AudioAssets.ExecuteDeleteAsync(Token);
        Assert.True(await new AudioRecordExistenceRepository(db).HasAnyAsync(Token));
    }

    private static async Task<string> Capture(AudioTestDatabase file)
    {
        var data = new List<object?>();
        foreach (var row in await file.RowsAsync("SELECT name FROM sqlite_master WHERE type='table' AND name != '__EFMigrationsHistory' AND name != 'audio_invalidation' ORDER BY name"))
        {
            var table = (string)((object?[])row!)[0]!; var quoted = '"' + table.Replace("\"", "\"\"") + '"';
            var columns = await file.RowsAsync($"PRAGMA table_info({quoted})");
            data.Add(await file.RowsAsync($"SELECT * FROM {quoted} ORDER BY {string.Join(',', Enumerable.Range(1, columns.Length))}"));
        }
        return JsonSerializer.Serialize(data);
    }
    private sealed class FailTrigger : DbCommandInterceptor
    {
        public bool Triggered;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE TRIGGER audio_invalidation_authorize", StringComparison.Ordinal))
            { Triggered = true; throw new InvalidOperationException("Injected migration failure."); }
            return ValueTask.FromResult(result);
        }
    }
}
