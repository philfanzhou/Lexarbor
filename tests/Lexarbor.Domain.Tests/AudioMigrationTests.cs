using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioMigrationTests
{
    private const string Previous = "20261001125645_AddServiceAuditLogs";
    private static readonly string[] OldTables = ["vocabulary", "vocabulary_book", "vocabulary_meaning", "vocabulary_book_unit", "vocabulary_meaning_unit", "admin_session", "service_data_protection_keys", "service_audit_logs"];

    [Fact]
    public async Task UpgradeFreshAndDown_KeepEveryOldRowAndSchema()
    {
        using var upgraded = new AudioTestDatabase();
        using var fresh = new AudioTestDatabase();
        await using var db = upgraded.Open();
        await db.GetService<IMigrator>().MigrateAsync(Previous, AudioTestDatabase.Token);
        await AudioTestDatabase.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO admin_session VALUES ('test-session-digest', 100, 'test-encrypted-payload');
            INSERT INTO service_data_protection_keys VALUES ('test-service', 'test-key', 'test-envelope');
            INSERT INTO service_audit_logs (id, operator_source, action, target_type, target_id, outcome, occurred_at_utc)
            VALUES ('00000000-0000-0000-0000-000000000001', 'test', 'test.action', 'test', 'test-target', 0, '2026-01-01');
            """, AudioTestDatabase.Token);
        var before = await CaptureOldAsync(upgraded);
        var schemaBefore = JsonSerializer.Serialize(await upgraded.RowsAsync("SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory' ORDER BY name"));
        await db.Database.MigrateAsync(AudioTestDatabase.Token);
        Assert.Equal(before, await CaptureOldAsync(upgraded));
        await using (var newDb = fresh.Open()) await newDb.Database.MigrateAsync(AudioTestDatabase.Token);
        const string schema = "SELECT type,name,sql FROM sqlite_master WHERE tbl_name IN ('audio_asset','audio_upload_operation','vocabulary_audio_binding') ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(schema)), JsonSerializer.Serialize(await upgraded.RowsAsync(schema)));
        db.AudioAssets.Add(AudioTestDatabase.Asset("old-audio"));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("old-audio", AudioLocale.EnGb, null, AudioTestDatabase.Hash));
        db.AudioUploadOperations.Add(AudioTestDatabase.Operation("old-audio", AudioUploadState.Succeeded));
        await db.SaveChangesAsync(AudioTestDatabase.Token);
        await db.GetService<IMigrator>().MigrateAsync(Previous, AudioTestDatabase.Token);
        Assert.Equal(before, await CaptureOldAsync(upgraded));
        Assert.Equal(schemaBefore, JsonSerializer.Serialize(await upgraded.RowsAsync("SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory' ORDER BY name")));
        Assert.Empty(await upgraded.RowsAsync("SELECT name FROM sqlite_master WHERE name IN ('audio_asset','audio_upload_operation','vocabulary_audio_binding','IX_vocabulary_meaning_id_vocabulary_id')"));
        await db.Database.MigrateAsync(AudioTestDatabase.Token);
        Assert.Equal(before, await CaptureOldAsync(upgraded));
        Assert.Empty(await upgraded.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task RealConstraints_RejectDuplicateTargetsWrongOwnershipInvalidLocaleAndBrokenReceipts()
    {
        using var file = new AudioTestDatabase();
        await using var db = file.Open(); await db.Database.MigrateAsync(AudioTestDatabase.Token);
        await AudioTestDatabase.SeedAsync(db);
        foreach (var id in new[] { "gb", "us", "meaning-gb", "meaning-us" }) db.AudioAssets.Add(AudioTestDatabase.Asset(id));
        db.VocabularyAudioBindings.AddRange(AudioTestDatabase.Binding("gb", AudioLocale.EnGb, null, AudioTestDatabase.Hash),
            AudioTestDatabase.Binding("us", AudioLocale.EnUs, null, AudioTestDatabase.Hash),
            AudioTestDatabase.Binding("meaning-gb", AudioLocale.EnGb, "meaning", AudioTestDatabase.Hash),
            AudioTestDatabase.Binding("meaning-us", AudioLocale.EnUs, "meaning", AudioTestDatabase.Hash));
        await db.SaveChangesAsync(AudioTestDatabase.Token);
        Assert.Equal(4, await db.VocabularyAudioBindings.CountAsync(AudioTestDatabase.Token));
        foreach (var target in new (string? Meaning, string Locale)[] { (null, "en-GB"), ("meaning", "en-US"), (null, "en-AU"), ("other-meaning", "en-GB"), ("missing", "en-US") })
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO vocabulary_audio_binding VALUES ('invalid','word',{target.Meaning},{target.Locale},'gb',{AudioTestDatabase.Hash})", AudioTestDatabase.Token));
            Assert.Equal(19, error.SqliteErrorCode);
        }
        foreach (var state in Enum.GetValues<AudioUploadState>())
        {
            var id = state.ToString(); db.AudioAssets.Add(AudioTestDatabase.Asset(id, state == AudioUploadState.Succeeded));
            db.AudioUploadOperations.Add(AudioTestDatabase.Operation(id, state));
        }
        await db.SaveChangesAsync(AudioTestDatabase.Token);
        Assert.Equal(6, await db.AudioUploadOperations.CountAsync(AudioTestDatabase.Token));
        foreach (var update in new[] { "state='unknown'", "locale='en-AU'", "receipt_status_code=NULL", "receipt_asset_id=NULL", "receipt_source_revision=NULL", "completed_at_unix_ms=NULL" })
        {
            // Fragments are fixed test cases, never request input.
            var sql = $"UPDATE audio_upload_operation SET {update} WHERE state='Succeeded'";
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, AudioTestDatabase.Token));
        }
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_upload_operation SET upload_lease_owner=NULL WHERE state='Reserved'", AudioTestDatabase.Token));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_upload_operation SET cleanup_lease_fence=0 WHERE state='CleanupClaimed'", AudioTestDatabase.Token));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audio_asset WHERE id='gb'", AudioTestDatabase.Token));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audio_asset WHERE id='Abandoned'", AudioTestDatabase.Token));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning WHERE id='meaning'", AudioTestDatabase.Token);
        Assert.Equal(2, await db.VocabularyAudioBindings.CountAsync(AudioTestDatabase.Token));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='word'", AudioTestDatabase.Token);
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(AudioTestDatabase.Token));
        Assert.Equal(10, await db.AudioAssets.CountAsync(AudioTestDatabase.Token));
        Assert.Equal(6, await db.AudioUploadOperations.CountAsync(AudioTestDatabase.Token));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task FailedSerializedTransaction_LeavesNoPartialReferenceModel()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(AudioTestDatabase.Token); await AudioTestDatabase.SeedAsync(db);
        var uow = new Lexarbor.Database.Repositories.UnitOfWork(db);
        await Assert.ThrowsAsync<Lexarbor.Domain.Exceptions.ConflictException>(() => uow.ExecuteInTransactionAsync(async () =>
        {
            db.AudioAssets.Add(AudioTestDatabase.Asset("rollback"));
            db.AudioUploadOperations.Add(AudioTestDatabase.Operation("rollback", AudioUploadState.Reserved));
            await uow.SaveChangesAsync();
            db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("rollback", AudioLocale.EnGb, "other-meaning", AudioTestDatabase.Hash));
            return await uow.SaveChangesAsync();
        }));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.AudioAssets.ToListAsync(AudioTestDatabase.Token));
        Assert.Empty(await db.AudioUploadOperations.ToListAsync(AudioTestDatabase.Token));
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(AudioTestDatabase.Token));
    }

    private static async Task<string> CaptureOldAsync(AudioTestDatabase file)
    {
        var all = new List<object?>();
        foreach (var table in OldTables)
        {
            // Later migrations may add meaning columns; this historical test
            // compares every column that existed before AddVocabularyAudio.
            var columns = table == "vocabulary_meaning"
                ? "id,vocabulary_id,book_id,part_of_speech,meaning,normalized_part_of_speech,normalized_meaning,example,created_at,updated_at"
                : "*";
            all.Add(await file.RowsAsync($"SELECT {columns} FROM {table} ORDER BY 1"));
        }
        return JsonSerializer.Serialize(all);
    }
}
