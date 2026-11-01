using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class MeaningPhoneticEpochMigrationTests
{
    private const string Previous = "20261101185722_AddAudioInvalidations";
    private static CancellationToken Token => AudioTestDatabase.Token;

    [Fact]
    public async Task UpgradeFreshAndDown_PreserveOldRowsReceiptsAndV1Input_AndOnlyDiscardEpochs()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token); await AudioTestDatabase.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='A' WHERE id='meaning'", Token);
        var source = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("word", "Apple", "uk", "us"),
            new("meaning", "n.", "fruit", "A"), AudioLocale.EnGb));
        var input = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new object?[] { "lexarbor.enrichment.input.v1", source, "Audio", null })));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO enrichment_target (id,word_id,meaning_id,locale,step,current_round,epoch,deleted)
            VALUES ('old-task','word','meaning','en-GB','Audio',1,1,0);
            INSERT INTO enrichment_round (target_id,number,state,input_revision,source_revision,own_asset_id,configuration_version,
              failure_budget,request_count,consumed_failures,lease_fence,created_at_unix_ms,updated_at_unix_ms)
            VALUES ('old-task',1,'Pending',{input},{source},'retained','config',5,0,0,0,1,1);
            """, Token);
        db.AudioAssets.Add(AudioTestDatabase.Asset("retained"));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("retained", AudioLocale.EnGb, "meaning", AudioTestDatabase.Hash));
        await db.SaveChangesAsync(Token); await AudioTestDatabase.SeedSucceededOperationAsync(db, "retained"); db.ChangeTracker.Clear();
        var before = await Capture(file); await db.Database.MigrateAsync(Token);
        Assert.Equal(before, await Capture(file)); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.MeaningPhoneticEpochs.ToListAsync(Token));
        Assert.Equal(0, (await db.AudioUploadOperations.SingleAsync(Token)).MeaningPhoneticEpoch);
        var repo = new MeaningPhoneticRepository(db, new AudioInvalidationRepository(db), new EnrichmentTaskRepository(db));
        var expected = await repo.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Token);
        Assert.Equal(source, expected.SourceRevision); Assert.Equal(0, expected.Epoch);
        var oldTask = (await new EnrichmentTaskRepository(db).EnsureAsync(new("word", "meaning", AudioLocale.EnGb, EnrichmentStep.Audio), "config", 10, Token))!;
        Assert.Equal("old-task", oldTask.Id); Assert.Equal(input, Assert.Single(oldTask.Rounds).Input.Revision);
        await new MeaningPhoneticEditService(repo, new UnitOfWork(db)).ApplyAsync(
            [new("book", "word", "meaning", AudioLocale.EnGb, true, "specific", expected)], new("config", "config", "config"), 10, Token);
        Assert.Single(await db.MeaningPhoneticEpochs.ToListAsync(Token));
        using var fresh = new AudioTestDatabase(); await using var freshDb = fresh.Open(); await freshDb.Database.MigrateAsync(Token);
        const string schema = "SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(schema)), JsonSerializer.Serialize(await file.RowsAsync(schema)));
        var receipt = (await new AudioUploadRepository(db).GetAsync((await db.AudioUploadOperations.SingleAsync(Token)).IdempotencyKey, Token))!.Operation.Receipt;
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name='meaning_phonetic_epoch'"));
        Assert.Empty(await file.RowsAsync("SELECT name FROM pragma_table_info('audio_upload_operation') WHERE name='meaning_phonetic_epoch'"));
        Assert.Equal("specific", ((object?[])(await file.RowsAsync("SELECT phonetic_uk FROM vocabulary_meaning WHERE id='meaning'"))[0]!)[0]);
        Assert.Single(await file.RowsAsync("SELECT * FROM audio_invalidation"));
        await db.Database.MigrateAsync(Token);
        Assert.Empty(await db.MeaningPhoneticEpochs.ToListAsync(Token));
        Assert.Equal(receipt, (await new AudioUploadRepository(db).GetAsync((await db.AudioUploadOperations.SingleAsync(Token)).IdempotencyKey, Token))!.Operation.Receipt);
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task FailureAfterColumnAddition_RollsBackSchemaAndHistoryThenCanResume()
    {
        using var file = new AudioTestDatabase();
        await using (var db = file.Open()) { await db.GetService<IMigrator>().MigrateAsync(Previous, Token); await AudioTestDatabase.SeedAsync(db); }
        var before = await Capture(file); var fault = new Failure();
        await using (var failing = file.Open(fault)) await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Database.MigrateAsync(Token));
        Assert.True(fault.Triggered); Assert.Equal(before, await Capture(file));
        Assert.Empty(await file.RowsAsync("SELECT name FROM pragma_table_info('audio_upload_operation') WHERE name='meaning_phonetic_epoch'"));
        Assert.Empty(await file.RowsAsync("SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId LIKE '%AddMeaningPhoneticEpochs'"));
        await using var resumed = file.Open(); await resumed.Database.MigrateAsync(Token);
        Assert.Equal(before, await Capture(file));
    }

    private static async Task<string> Capture(AudioTestDatabase file)
    {
        var result = new List<object?>();
        foreach (var table in new[] { "vocabulary", "vocabulary_meaning", "audio_asset", "audio_upload_operation", "vocabulary_audio_binding", "audio_invalidation", "enrichment_target", "enrichment_round" })
            result.Add(await file.RowsAsync($"SELECT {await file.PreEpochColumnsAsync(table)} FROM {table} ORDER BY 1"));
        return JsonSerializer.Serialize(result);
    }
    private sealed class Failure : DbCommandInterceptor
    {
        public bool Triggered;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("CREATE TABLE \"meaning_phonetic_epoch\"", StringComparison.Ordinal))
            { Triggered = true; throw new InvalidOperationException("Injected epoch migration failure."); }
            return ValueTask.FromResult(result);
        }
    }
}
