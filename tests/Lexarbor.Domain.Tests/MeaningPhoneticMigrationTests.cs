using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class MeaningPhoneticMigrationTests
{
    private const string Previous = "20261031172216_AddMeaningSubmissions";
    private const string Migration = "20261101125550_AddMeaningPhonetics";
    private static CancellationToken Token => AudioTestDatabase.Token;

    [Fact]
    public async Task UpgradeAndDown_KeepOldDataReceiptsAndRevisionsButStopSelectingLegacyOverride()
    {
        using var file = new AudioTestDatabase();
        await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.AddRange(AudioTestDatabase.Asset("default"), AudioTestDatabase.Asset("legacy"));
        db.VocabularyAudioBindings.AddRange(
            AudioTestDatabase.Binding("default", AudioLocale.EnGb, null, AudioTestDatabase.Revision(AudioLocale.EnGb)),
            AudioTestDatabase.Binding("legacy", AudioLocale.EnGb, "meaning", AudioTestDatabase.Revision(AudioLocale.EnGb, true)));
        db.AudioUploadOperations.Add(AudioTestDatabase.Operation("legacy", AudioUploadState.Succeeded));
        await db.SaveChangesAsync(Token);
        var before = await CaptureOldAsync(file);

        await db.GetService<IMigrator>().MigrateAsync(Migration, Token);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(before, await CaptureOldAsync(file));
        Assert.Equal("[[null,null],[null,null]]", JsonSerializer.Serialize(await file.RowsAsync(
            "SELECT phonetic_uk,phonetic_us FROM vocabulary_meaning ORDER BY id")));
        var repository = new AudioReadRepository(db);
        var snapshot = await repository.GetSnapshotAsync("word", "meaning", Token);
        var selection = AudioSelectionService.Select(snapshot, AudioLocale.EnGb);
        Assert.Equal(AudioTestDatabase.Revision(AudioLocale.EnGb, true), selection.SourceRevision);
        Assert.Equal("legacy", selection.OwnBinding!.Asset.Id);
        Assert.Equal("default", selection.EffectiveBinding!.Asset.Id);
        Assert.Equal(AudioBindingSource.Default, selection.Source);

        var meanings = new VocabularyMeaningRepository(db);
        var meaning = Assert.IsType<VocabularyMeaningModel>(await meanings.GetByIdAsync("meaning"));
        meaning.PhoneticUk = " /uk/ "; meaning.PhoneticUs = "specific-us";
        await meanings.UpdateAsync(meaning); await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        var reread = Assert.IsType<VocabularyMeaningModel>(await meanings.GetByIdAsync("meaning"));
        Assert.Equal(" /uk/ ", reread.PhoneticUk); Assert.Equal("specific-us", reread.PhoneticUs);

        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.Empty(await file.RowsAsync("SELECT name FROM pragma_table_info('vocabulary_meaning') WHERE name IN ('phonetic_uk','phonetic_us')"));
        Assert.Equal(before, await CaptureOldAsync(file));
        await db.GetService<IMigrator>().MigrateAsync(Migration, Token);
        Assert.Equal(before, await CaptureOldAsync(file));
        Assert.Equal("[[null,null],[null,null]]", JsonSerializer.Serialize(await file.RowsAsync(
            "SELECT phonetic_uk,phonetic_us FROM vocabulary_meaning ORDER BY id")));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));

        using var fresh = new AudioTestDatabase();
        await using var freshDb = fresh.Open();
        await freshDb.GetService<IMigrator>().MigrateAsync(Migration, Token);
        const string schema = "SELECT type,name,sql FROM sqlite_master WHERE tbl_name='vocabulary_meaning' ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(schema)), JsonSerializer.Serialize(await file.RowsAsync(schema)));
    }

    [Fact]
    public async Task FailedUpgrade_RollsBackBothColumnsAndHistoryAndCanResume()
    {
        using var file = new AudioTestDatabase();
        await using (var seed = file.Open())
        {
            await seed.GetService<IMigrator>().MigrateAsync(Previous, Token);
            await AudioTestDatabase.SeedAsync(seed);
        }
        var before = await CaptureOldAsync(file);
        var fault = new FailSecondColumn();
        await using (var db = file.Open(fault))
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.GetService<IMigrator>().MigrateAsync(Migration, Token));
        Assert.True(fault.Triggered);
        Assert.Empty(await file.RowsAsync("SELECT name FROM pragma_table_info('vocabulary_meaning') WHERE name IN ('phonetic_uk','phonetic_us')"));
        Assert.Empty(await file.RowsAsync($"SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId='{Migration}'"));
        Assert.Equal(before, await CaptureOldAsync(file));
        await using var resumed = file.Open();
        await resumed.GetService<IMigrator>().MigrateAsync(Migration, Token);
        Assert.Equal(before, await CaptureOldAsync(file));
    }

    private static async Task<string> CaptureOldAsync(AudioTestDatabase file)
    {
        var all = new List<object?>();
        foreach (var table in new[] { "vocabulary", "vocabulary_book", "vocabulary_meaning", "vocabulary_book_unit",
            "vocabulary_meaning_unit", "audio_asset", "vocabulary_audio_binding", "audio_upload_operation", "meaning_submission" })
        {
            var columns = table == "vocabulary_meaning"
                ? "id,vocabulary_id,book_id,part_of_speech,meaning,normalized_part_of_speech,normalized_meaning,example,created_at,updated_at"
                : "*";
            all.Add(await file.RowsAsync($"SELECT {columns} FROM {table} ORDER BY 1"));
        }
        return JsonSerializer.Serialize(all);
    }

    private sealed class FailSecondColumn : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("ALTER TABLE", StringComparison.Ordinal)
                && command.CommandText.Contains("ADD \"phonetic_us\"", StringComparison.Ordinal))
            {
                Triggered = true;
                throw new InvalidOperationException("Injected second phonetic column failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
