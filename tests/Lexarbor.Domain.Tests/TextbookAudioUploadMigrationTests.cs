using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class TextbookAudioUploadMigrationTests
{
    private const string Previous = "20261017235129_AddTextbookAudioTracks";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] OldTables = ["vocabulary", "vocabulary_book", "vocabulary_meaning", "vocabulary_book_unit", "vocabulary_meaning_unit", "admin_session", "service_data_protection_keys", "service_audit_logs", "audio_asset", "audio_upload_operation", "vocabulary_audio_binding", "textbook_audio_track"];

    [Fact]
    public async Task UpgradeFreshAndDown_KeepAllExistingRowsSchemaAndDirectoryDeleteGuards()
    {
        using var file = new AudioTestDatabase(); using var fresh = new AudioTestDatabase(); await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token); await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.Add(AudioTestDatabase.Asset("legacy")); db.AudioUploadOperations.Add(AudioTestDatabase.Operation("legacy", AudioUploadState.Succeeded));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("legacy", AudioLocale.EnGb, "meaning", AudioTestDatabase.Hash));
        db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "book", UnitId = "unit", Title = "Retained", Version = TextbookAudioUploadRepositoryTests.Version, CurrentAssetId = "legacy", DurationSeconds = 42, IsAvailable = true });
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO admin_session VALUES ('synthetic-session',100,'synthetic-envelope');
            INSERT INTO service_data_protection_keys VALUES ('synthetic-service','synthetic-key','synthetic-envelope');
            INSERT INTO service_audit_logs (id,operator_source,action,target_type,target_id,outcome,occurred_at_utc)
            VALUES ('00000000-0000-0000-0000-000000000001','test','test.action','test','synthetic',0,'2026-01-01');
            """, Token);
        var before = await CaptureAsync(file); var schemaBefore = await SchemaAsync(file);
        await db.Database.MigrateAsync(Token); Assert.Equal(before, await CaptureAsync(file));
        await using (var newDb = fresh.Open()) await newDb.Database.MigrateAsync(Token);
        const string newSchema = "SELECT type,name,sql FROM sqlite_master WHERE tbl_name='textbook_audio_upload_operation' ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(newSchema)), JsonSerializer.Serialize(await file.RowsAsync(newSchema)));
        foreach (var sql in new[] { "DELETE FROM vocabulary_book WHERE id='book'", "DELETE FROM vocabulary_book_unit WHERE id='unit'", "DELETE FROM audio_asset WHERE id='legacy'" })
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, Token));
        // Down removes only this table and its indexes, retaining every old structure/row.
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.Equal(before, await CaptureAsync(file)); Assert.Equal(schemaBefore, await SchemaAsync(file));
        await db.Database.MigrateAsync(Token);
        var repo = new TextbookAudioUploadRepository(db, new FixedClock());
        var upload = (await repo.ReserveAsync(TextbookAudioUploadRepositoryTests.Request(), "owner", 3, 100, Token)).Upload!;
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.True(await db.AudioAssets.AnyAsync(x => x.Id == upload.Asset.Id, Token));
        Assert.Equal(1, await db.AudioUploadOperations.CountAsync(Token)); Assert.Single(await db.TextbookAudioTracks.ToListAsync(Token));
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE tbl_name='textbook_audio_upload_operation'"));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task ActualSqliteChecks_RejectMalformedReceiptsLeasesStatesAndAssetRelationships()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await TextbookAudioUploadRepositoryTests.InitAsync(db);
        var repo = new TextbookAudioUploadRepository(db, new FixedClock());
        var request = TextbookAudioUploadRepositoryTests.Request(); var upload = (await repo.ReserveAsync(request, "owner", 1, 100, Token)).Upload!;
        foreach (var update in new[] {
            "idempotency_key='bad'", "idempotency_key='AAAAAAAA-1111-1111-1111-111111111111'", "expected_version='bad'", "request_fingerprint='bad'", "book_id=' '", "track_id=' track '",
            "duration_seconds=0", "duration_seconds=1e999", "state='unknown'", "upload_lease_owner=NULL", "upload_lease_owner=' '", "upload_lease_fence=0", "upload_lease_expires_at_unix_ms=NULL",
            "cleanup_return_state='Abandoned'", "cleanup_lease_owner='other'", "cleanup_lease_fence=-1", "cleanup_attempts=-1", "updated_at_unix_ms=0", "receipt_status_code=200", "asset_id='missing'" })
            await RejectAsync(db, update, request.IdempotencyKey);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM audio_asset WHERE id={upload.Asset.Id}", Token));
        await repo.BeginTransferAsync(request.IdempotencyKey, upload.Operation.UploadLease, 2, Token);
        await repo.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, 3, Token);
        foreach (var update in new[] { "receipt_status_code=NULL", "receipt_asset_id=NULL", "receipt_asset_id='other'", "receipt_track_id=NULL", "receipt_track_id='other'", "receipt_version=NULL", "receipt_version='bad'", "receipt_duration_seconds=NULL", "receipt_duration_seconds=99", "completed_at_unix_ms=NULL", "receipt_error_code='audio.upload_failed'", "upload_lease_owner='owner'" })
            await RejectAsync(db, update, request.IdempotencyKey);
        // Two textbook operations cannot claim the same independent asset.
        var nextRequest = TextbookAudioUploadRepositoryTests.Request() with { ExpectedVersion = (await repo.GetAsync(request.IdempotencyKey, Token))!.Operation.Receipt!.Version! };
        var failed = (await repo.ReserveAsync(nextRequest, "owner", 4, 100, Token)).Upload!;
        await RejectAsync(db, $"asset_id='{upload.Asset.Id}'", nextRequest.IdempotencyKey);
        await repo.AbandonAsync(nextRequest.IdempotencyKey, failed.Operation.UploadLease, 5, 503, "audio.upload_failed", Token);
        foreach (var update in new[] { "receipt_status_code=500", "receipt_error_code=NULL", "receipt_error_code='sensitive-value'", "receipt_version='11111111-1111-1111-1111-111111111111'", "receipt_asset_id='other'", "state='Succeeded'" })
            await RejectAsync(db, update, nextRequest.IdempotencyKey);
        var claimed = (await repo.ClaimCleanupAsync(nextRequest.IdempotencyKey, "cleaner", 86_400_004, 86_400_104, Token))!;
        foreach (var update in new[] { "cleanup_lease_owner=NULL", "cleanup_lease_expires_at_unix_ms=NULL", "cleanup_lease_fence=0", "cleanup_return_state=NULL", "cleanup_return_state='Reserved'", "cleanup_lease_expires_at_unix_ms=updated_at_unix_ms" })
            await RejectAsync(db, update, nextRequest.IdempotencyKey);
        Assert.Equal(503, claimed.Operation.Receipt!.StatusCode); Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    private static async Task RejectAsync(VocabularyDbContext db, string update, string key)
    {
        // All fragments are fixed synthetic test cases, never external input.
        var sql = $"UPDATE textbook_audio_upload_operation SET {update} WHERE idempotency_key={{0}}";
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, new object[] { key }, Token));
        Assert.Equal(19, error.SqliteErrorCode);
    }
    private static async Task<string> CaptureAsync(AudioTestDatabase file)
    {
        var rows = new List<object?>(); foreach (var table in OldTables) rows.Add(await file.RowsAsync($"SELECT * FROM {table} ORDER BY 1"));
        return JsonSerializer.Serialize(rows);
    }
    private static async Task<string> SchemaAsync(AudioTestDatabase file) => JsonSerializer.Serialize(await file.RowsAsync("SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND tbl_name != '__EFMigrationsHistory' ORDER BY type,name"));
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch; }
}
