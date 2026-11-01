using System.Text.Json;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class TextbookAudioRemovalMigrationTests
{
    private const string Previous = "20261018010154_AddTextbookAudioUploads";
    private const string Version = "11111111-1111-1111-1111-111111111111";
    private static CancellationToken Ct => AudioTestDatabase.Token;

    [Fact]
    public async Task Upgrade_RemovesCatalogAndReceipts_PreservesPronunciationAndAllAssets()
    {
        using var file = new AudioTestDatabase();
        using var fresh = new AudioTestDatabase();
        await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(Previous, Ct);
        await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.AddRange(AudioTestDatabase.Asset("pronunciation"), AudioTestDatabase.Asset("recording"),
            AudioTestDatabase.Asset("pending-recording", published: false));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("pronunciation", AudioLocale.EnGb, null,
            AudioTestDatabase.Revision(AudioLocale.EnGb)));
        db.AudioUploadOperations.Add(AudioTestDatabase.Operation("pronunciation", AudioUploadState.Succeeded));
        await db.SaveChangesAsync(Ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO textbook_audio_track
                (id,book_id,unit_id,title,display_order,current_asset_id,duration_seconds,is_available,version)
            VALUES ('track','book','unit','Recording',0,'recording',42,1,{Version});
            """, Ct);
        foreach (var state in Enum.GetValues<AudioUploadState>())
        {
            // Use the historical SQL contract: removed entities must not re-enter the model.
            var assetId = "recording-" + state;
            db.AudioAssets.Add(AudioTestDatabase.Asset(assetId, state == AudioUploadState.Succeeded));
            await db.SaveChangesAsync(Ct);
            var pending = state is AudioUploadState.Reserved or AudioUploadState.Transferring;
            var succeeded = state == AudioUploadState.Succeeded;
            var cleaning = state == AudioUploadState.CleanupClaimed;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO textbook_audio_upload_operation
                    (idempotency_key,request_fingerprint,book_id,unit_id,track_id,expected_version,duration_seconds,
                     asset_id,state,created_at_unix_ms,updated_at_unix_ms,upload_lease_owner,upload_lease_fence,
                     upload_lease_expires_at_unix_ms,receipt_status_code,receipt_asset_id,receipt_track_id,
                     receipt_version,receipt_duration_seconds,receipt_error_code,completed_at_unix_ms,
                     cleanup_lease_owner,cleanup_lease_fence,cleanup_lease_expires_at_unix_ms,cleanup_return_state,cleanup_attempts)
                VALUES ({Guid.NewGuid().ToString()},{AudioTestDatabase.Hash},'book','unit','track',{Version},42,
                    {assetId},{state.ToString()},1,2,{(pending ? "uploader" : null)},1,{(pending ? (long?)100 : null)},
                    {(pending ? (int?)null : succeeded ? 200 : 503)},{(succeeded ? assetId : null)},
                    {(succeeded ? "track" : null)},{(succeeded ? Version : null)},{(succeeded ? (double?)42 : null)},
                    {(pending || succeeded ? null : "audio.upload_failed")},{(pending ? (long?)null : 2)},
                    {(cleaning ? "cleaner" : null)},{(cleaning ? 1 : 0)},{(cleaning ? (long?)100 : null)},
                    {(cleaning ? "Abandoned" : null)},0);
                """, Ct);
        }

        var before = await CaptureRetainedAsync(file);
        await db.Database.MigrateAsync(Ct);
        Assert.Equal(before, await CaptureRetainedAsync(file));
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name LIKE 'textbook_audio_%'"));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
        Assert.True(await new AudioRecordExistenceRepository(db).HasAnyAsync(Ct));
        Assert.DoesNotContain(db.Model.GetEntityTypes(), entity => entity.GetTableName()!.StartsWith("textbook_audio_"));

        // Verify the entire fresh schema, including triggers, matches the upgraded schema.
        await using (var newDb = fresh.Open()) await newDb.Database.MigrateAsync(Ct);
        const string schema = "SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(schema)), JsonSerializer.Serialize(await file.RowsAsync(schema)));

        // The retired audio_asset trigger must not break later pronunciation writes.
        await db.Database.ExecuteSqlRawAsync("UPDATE audio_asset SET first_published_at_unix_ms=NULL WHERE id='pending-recording'", Ct);
        var units = new VocabularyBookUnitDomainService(new VocabularyBookRepository(db),
            new VocabularyMeaningRepository(db), new VocabularyBookUnitRepository(db),
            new VocabularyMeaningUnitRepository(db), new UnitOfWork(db));
        await units.DeleteAsync("book", "unit");
        var cleanup = new VocabularyCleanupService(new VocabularyCleanupRepository(db), new UnitOfWork(db));
        Assert.Equal("delete", (await cleanup.PreviewAsync("book", new("delete"), Ct)).Action);
        // The retained recording assets alone never prevent normal vocabulary cleanup.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning WHERE book_id='book'", Ct);
        await new VocabularyBookDomainService(new VocabularyBookRepository(db), new UnitOfWork(db)).DeleteAsync("book");
        Assert.Equal(9, await db.AudioAssets.CountAsync(Ct));
        Assert.Single(await db.VocabularyAudioBindings.ToListAsync(Ct));
        Assert.Single(await db.AudioUploadOperations.ToListAsync(Ct));
    }

    [Fact]
    public async Task SchemaRollback_RestoresConstraintsAndTriggers_ButCannotRestoreDeletedRows()
    {
        using var file = new AudioTestDatabase();
        await using var db = file.Open();
        await db.Database.MigrateAsync(Ct);
        await AudioTestDatabase.SeedAsync(db);
        await db.GetService<IMigrator>().MigrateAsync(Previous, Ct);
        Assert.Empty(await file.RowsAsync("SELECT * FROM textbook_audio_track"));
        Assert.Empty(await file.RowsAsync("SELECT * FROM textbook_audio_upload_operation"));
        Assert.Equal(3, (await file.RowsAsync("SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'textbook_audio_%'")).Length);
        db.AudioAssets.Add(AudioTestDatabase.Asset("unpublished", published: false));
        await db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO textbook_audio_track
                (id,book_id,unit_id,title,display_order,current_asset_id,duration_seconds,is_available,version)
            VALUES ('invalid','book','unit','Unpublished',0,'unpublished',42,1,{Version});
            """, Ct));
        await db.Database.MigrateAsync(Ct);
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name LIKE 'textbook_audio_%'"));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    private static async Task<string> CaptureRetainedAsync(AudioTestDatabase file)
    {
        var rows = new List<object?[]>();
        foreach (var table in new[] { "vocabulary_book", "vocabulary_book_unit", "vocabulary", "vocabulary_meaning",
                     "vocabulary_meaning_unit", "audio_asset", "vocabulary_audio_binding", "audio_upload_operation" })
        {
            var columns = table == "vocabulary_meaning"
                ? "id,vocabulary_id,book_id,part_of_speech,meaning,normalized_part_of_speech,normalized_meaning,example,created_at,updated_at"
                : "*";
            rows.Add(await file.RowsAsync($"SELECT {columns} FROM {table} ORDER BY 1"));
        }
        return JsonSerializer.Serialize(rows);
    }
}
