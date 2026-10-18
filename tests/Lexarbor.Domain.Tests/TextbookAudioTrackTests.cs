using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class TextbookAudioTrackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Version = "00000000-0000-0000-0000-000000000001";
    private static TextbookAudioTrackService Service(VocabularyDbContext db) => new(new TextbookAudioTrackRepository(db), new UnitOfWork(db));
    private static VocabularyBookUnitDomainService Units(VocabularyDbContext db) => new(new VocabularyBookRepository(db),
        new VocabularyMeaningRepository(db), new VocabularyBookUnitRepository(db), new VocabularyMeaningUnitRepository(db), new UnitOfWork(db));

    [Fact]
    public async Task UpgradeFreshRestartAndDown_PreserveEveryExistingTable()
    {
        using var old = new AudioTestDatabase(); using var fresh = new AudioTestDatabase();
        await using (var db = old.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261011163230_AddVocabularyAudio", Token);
            await AudioTestDatabase.SeedAsync(db);
            db.AudioAssets.Add(AudioTestDatabase.Asset("retained"));
            db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("retained", AudioLocale.EnGb, null, AudioTestDatabase.Hash));
            db.AudioUploadOperations.Add(AudioTestDatabase.Operation("retained", AudioUploadState.Succeeded));
            await db.SaveChangesAsync(Token);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO admin_session VALUES ('test-digest',100,'test-payload');
                INSERT INTO service_data_protection_keys VALUES ('test-service','test-key','test-envelope');
                INSERT INTO service_audit_logs (id,operator_source,action,target_type,target_id,outcome,occurred_at_utc)
                VALUES ('00000000-0000-0000-0000-000000000001','test','test.action','test','test-target',0,'2026-01-01');
                """, Token);
        }
        var before = await CaptureAsync(old);
        await using (var db = old.Open()) { await db.Database.MigrateAsync(Token); Assert.False(db.Database.HasPendingModelChanges()); }
        Assert.Equal(before, await CaptureAsync(old));
        await using (var db = fresh.Open()) await db.Database.MigrateAsync(Token);
        const string schema = "SELECT type,name,sql FROM sqlite_master WHERE tbl_name='textbook_audio_track' ORDER BY type,name";
        Assert.Equal(JsonSerializer.Serialize(await fresh.RowsAsync(schema)), JsonSerializer.Serialize(await old.RowsAsync(schema)));
        await using (var restarted = old.Open())
        {
            await restarted.Database.MigrateAsync(Token);
            Assert.Empty(await restarted.TextbookAudioTracks.ToArrayAsync(Token));
            var track = await Service(restarted).CreateAsync("book", "unit", "recording", 0, Token);
            await restarted.GetService<IMigrator>().MigrateAsync("20261011163230_AddVocabularyAudio", Token);
            Assert.Equal(before, await CaptureAsync(old));
            Assert.Empty(await old.RowsAsync("SELECT name FROM sqlite_master WHERE name='textbook_audio_track'"));
            await restarted.Database.MigrateAsync(Token);
        }
        Assert.Equal(before, await CaptureAsync(old));
        Assert.Empty(await old.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task SqlConstraints_RejectInvalidOwnershipStateAndParentDeletion()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        db.VocabularyBooks.Add(new() { Id = "other-book", BookName = "Other" });
        db.AudioAssets.Add(AudioTestDatabase.Asset("asset")); await db.SaveChangesAsync(Token);
        foreach (var tail in new[]
        {
            "'other-book','unit','title',0,NULL,NULL,0", "'book','missing','title',0,NULL,NULL,0",
            "'book','unit',' ',0,NULL,NULL,0", "'book','unit','title',-1,NULL,NULL,0",
            "'book','unit','title',0,NULL,NULL,1", "'book','unit','title',0,NULL,1,0",
            "'book','unit','title',0,'asset',NULL,0", "'book','unit','title',0,'asset',0,0",
            "'book','unit','title',0,'asset',1e999,0", "'book','unit','title',0,'missing',1,0",
            "'book','unit','title',0,'asset',1,2"
        })
        {
            var sql = $"INSERT INTO textbook_audio_track(id,book_id,unit_id,title,display_order,current_asset_id,duration_seconds,is_available,version) VALUES ('invalid',{tail},'{Version}')";
            var ex = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, Token)); Assert.Equal(19, ex.SqliteErrorCode);
        }
        var track = await Service(db).CreateAsync("book", "unit", "valid", 0, Token);
        foreach (var version in new[] { "", "UNKNOWN", "00000000-0000-0000-0000-00000000000A" })
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE textbook_audio_track SET version={version}", Token));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning", Token);
        foreach (var sql in new[] { "DELETE FROM vocabulary_book WHERE id='book'", "DELETE FROM vocabulary_book_unit WHERE id='unit'" })
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, Token));
        await db.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET current_asset_id='asset',duration_seconds=0.5", Token);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audio_asset WHERE id='asset'", Token));
        db.AudioAssets.Add(AudioTestDatabase.Asset("unpublished", false)); await db.SaveChangesAsync(Token);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET current_asset_id='unpublished',duration_seconds=1,is_available=1", Token));
        await db.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET is_available=1", Token);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE audio_asset SET first_published_at_unix_ms=NULL WHERE id='asset'", Token));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task StablePaginationAndConditionalWrites_UseCurrentDatabaseValues()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        var service = Service(db); var all = new List<TextbookAudioTrackModel>();
        for (var i = 0; i < 3; i++) all.Add(await service.CreateAsync("book", "unit", " track ", 5, Token));
        var page = await service.ListAsync("book", "unit", 1, 2, Token);
        Assert.Equal(all.OrderBy(t => t.Id, StringComparer.Ordinal).Take(2).Select(t => t.Id), page.Items.Select(t => t.Id));
        Assert.Equal(3, page.TotalCount); Assert.Equal(2, page.TotalPage);
        Assert.Single((await service.ListAsync("book", "unit", 2, 2, Token)).Items);
        Assert.Empty((await service.ListAsync("book", "unit", int.MaxValue, 100, Token)).Items);
        var first = all[0];
        await Assert.ThrowsAsync<ConflictException>(() => service.ReplaceAsync("book", "unit", first.Id, "track", 5, true, first.Version, Token));
        // An old tracked entity must neither supply current file state nor overwrite it.
        _ = await db.TextbookAudioTracks.SingleAsync(t => t.Id == first.Id, Token);
        await using (var publisher = file.Open())
        {
            publisher.AudioAssets.Add(AudioTestDatabase.Asset("published")); await publisher.SaveChangesAsync(Token);
            await publisher.Database.ExecuteSqlInterpolatedAsync($"UPDATE textbook_audio_track SET current_asset_id='published',duration_seconds=1.25,version={Version} WHERE id={first.Id}", Token);
        }
        await Assert.ThrowsAsync<ConflictException>(() => service.ReplaceAsync("book", "unit", first.Id, "stale", 5, true, first.Version, Token));
        var enabled = await service.ReplaceAsync("book", "unit", first.Id, "track", 5, true, Version, Token);
        Assert.True(enabled.IsAvailable); Assert.Equal(1.25, enabled.DurationSeconds); Assert.NotEqual(first.Version, enabled.Version);
        await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync("book", "unit", first.Id, first.Version, Token));
        var same = await service.ReplaceAsync("book", "unit", first.Id, "track", 5, true, enabled.Version, Token);
        Assert.NotEqual(enabled.Version, same.Version);
        var disabled = await service.ReplaceAsync("book", "unit", first.Id, "track", 5, false, same.Version, Token);
        Assert.Equal("published", disabled.CurrentAssetId);
        db.AudioUploadOperations.Add(AudioTestDatabase.Operation("published", AudioUploadState.Succeeded)); await db.SaveChangesAsync(Token);
        await service.DeleteAsync("book", "unit", first.Id, disabled.Version, Token);
        Assert.Single(await db.AudioAssets.ToArrayAsync(Token)); Assert.Single(await db.AudioUploadOperations.ToArrayAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameVersionOnIndependentConnections_OnlyFirstWriterWins(bool deleteSecond)
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open();
        await setup.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(setup);
        var track = await Service(setup).CreateAsync("book", "unit", "original", 0, Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = file.Open(new PauseUpdate(entered, release)); await using var second = file.Open();
        var winner = Task.Run(() => Service(first).ReplaceAsync("book", "unit", track.Id, "winner", 2, false, track.Version, Token), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
        var loser = deleteSecond ? Service(second).DeleteAsync("book", "unit", track.Id, track.Version, Token)
            : (Task)Service(second).ReplaceAsync("book", "unit", track.Id, "loser", 3, false, track.Version, Token);
        Assert.False(loser.IsCompleted); release.SetResult(); await winner;
        await Assert.ThrowsAsync<ConflictException>(() => loser);
        var stored = await new TextbookAudioTrackRepository(second).GetAsync(track.Id, Token);
        Assert.Equal("winner", stored!.Title);
    }

    [Fact]
    public async Task AllParentDeletionPaths_RejectBeforeVocabularyChanges_AndOtherCleanupPreservesTracks()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        var track = await Service(db).CreateAsync("book", "unit", "track", 0, Token);
        db.AudioAssets.Add(AudioTestDatabase.Asset("keep-file"));
        db.AudioUploadOperations.Add(AudioTestDatabase.Operation("keep-file", AudioUploadState.Succeeded));
        await db.SaveChangesAsync(Token);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE textbook_audio_track SET current_asset_id='keep-file',duration_seconds=1.5 WHERE id={track.Id}", Token);
        track = (await new TextbookAudioTrackRepository(db).GetAsync(track.Id, Token))!;
        var before = await CaptureAsync(file);
        await Assert.ThrowsAsync<ConflictException>(() => new VocabularyBookDomainService(new VocabularyBookRepository(db), new UnitOfWork(db)).DeleteAsync("book"));
        await Assert.ThrowsAsync<ConflictException>(() => Units(db).DeleteAsync("book", "unit"));
        var cleanup = new VocabularyCleanupService(new VocabularyCleanupRepository(db), new UnitOfWork(db));
        var delete = new VocabularyCleanupSelection("delete", ConfirmedBookName: "Audio test");
        await Assert.ThrowsAsync<ConflictException>(() => cleanup.PreviewAsync("book", delete, Token));
        await Assert.ThrowsAsync<ConflictException>(() => cleanup.CommitAsync("book", delete, Token));
        Assert.Equal(before, await CaptureAsync(file));
        foreach (var selection in new[] { new VocabularyCleanupSelection("removeMeaning", WordId: "word", MeaningId: "meaning"),
                     new VocabularyCleanupSelection("removeWords", WordIds: ["other"]), new VocabularyCleanupSelection("clear") })
        {
            await cleanup.CommitAsync("book", selection, Token);
            Assert.Equal(track, (await Service(db).ListAsync("book", "unit", 0, 0, Token)).Items.Single());
        }
        await Service(db).DeleteAsync("book", "unit", track.Id, track.Version, Token);
        await Units(db).DeleteAsync("book", "unit");
        await cleanup.CommitAsync("book", delete, Token);
        Assert.Empty(await db.VocabularyBooks.ToArrayAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentDeletionAndCreate_AreOrderedWithoutDanglingRows(bool deleteFirst)
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open();
        await setup.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(setup);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = file.Open(new PauseMutation(entered, release)); await using var second = file.Open();
        var winner = Task.Run(async () => { if (deleteFirst) await Units(first).DeleteAsync("book", "unit"); else await Service(first).CreateAsync("book", "unit", "track", 0, Token); }, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
        var loser = deleteFirst ? (Task)Service(second).CreateAsync("book", "unit", "track", 0, Token) : Units(second).DeleteAsync("book", "unit");
        Assert.False(loser.IsCompleted); release.SetResult(); await winner;
        if (deleteFirst) await Assert.ThrowsAsync<ResourceNotFoundException>(() => loser);
        else await Assert.ThrowsAsync<ConflictException>(() => loser);
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
        Assert.Equal(deleteFirst ? 0 : 1, await second.TextbookAudioTracks.CountAsync(Token));
    }

    [Fact]
    public async Task SnapshotRead_DoesNotBlockWriter_AndKeepsCountAndPageTogether()
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open();
        await setup.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(setup);
        await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Token);
        await Service(setup).CreateAsync("book", "unit", "first", 0, Token);
        var counted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = file.Open(new PauseCount(counted, release)); await using var writer = file.Open();
        var read = Task.Run(() => Service(reader).ListAsync("book", "unit", 0, 0, Token), Token);
        await counted.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
        try { await Service(writer).CreateAsync("book", "unit", "second", 0, Token).WaitAsync(TimeSpan.FromSeconds(15), Token); }
        finally { release.TrySetResult(); }
        var result = await read; Assert.Equal(1, result.TotalCount); Assert.Single(result.Items);
        Assert.Equal(2, (await Service(writer).ListAsync("book", "unit", 0, 0, Token)).TotalCount);
    }

    [Theory]
    [InlineData("before-admission")]
    [InlineData("before-commit")]
    [InlineData("write-failure")]
    [InlineData("commit-failure")]
    public async Task CancellationAndFailures_RollBackAllNewRows(string failure)
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open();
        await setup.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(setup);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var interceptors = failure switch
        {
            "before-commit" => new IInterceptor[] { new CancelAfterSave(cts) },
            "write-failure" => [new ThrowOnSave()],
            "commit-failure" => [new ThrowOnCommit()],
            _ => []
        };
        await using var db = file.Open(interceptors);
        if (failure == "before-admission") cts.Cancel();
        if (failure is "before-admission" or "before-commit")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db).CreateAsync("book", "unit", "rollback", 0, cts.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateAsync("book", "unit", "rollback", 0, cts.Token));
        Assert.Empty(await setup.TextbookAudioTracks.AsNoTracking().ToArrayAsync(Token));
    }

    [Fact]
    public async Task CancellationWhileQueued_DoesNotWriteAfterAdmission()
    {
        using var file = new AudioTestDatabase(); await using var blocker = file.Open();
        await blocker.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(blocker);
        await using var writer = file.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = Task.Run(() => new UnitOfWork(blocker).ExecuteInTransactionAsync(async () =>
        { entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15), Token); return 0; }, Token), Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var queued = Service(writer).CreateAsync("book", "unit", "cancelled", 0, cts.Token);
            Assert.False(queued.IsCompleted); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }
        finally { release.TrySetResult(); await hold; }
        Assert.Empty(await writer.TextbookAudioTracks.AsNoTracking().ToArrayAsync(Token));
    }

    private static async Task<string> CaptureAsync(AudioTestDatabase file)
    {
        var data = new List<object?>();
        foreach (var name in new[] { "vocabulary", "vocabulary_book", "vocabulary_meaning", "vocabulary_book_unit", "vocabulary_meaning_unit", "admin_session", "service_data_protection_keys", "service_audit_logs", "audio_asset", "vocabulary_audio_binding", "audio_upload_operation" })
            data.Add(await file.RowsAsync($"SELECT * FROM {name} ORDER BY 1"));
        return JsonSerializer.Serialize(data);
    }
    private sealed class PauseUpdate(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.StartsWith("UPDATE \"textbook_audio_track\"", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); } return result; }
    }
    private sealed class PauseMutation(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.StartsWith("INSERT INTO \"textbook_audio_track\"", StringComparison.Ordinal) || command.CommandText.StartsWith("DELETE FROM \"vocabulary_book_unit\"", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); } return result; }
    }
    private sealed class PauseCount(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal) && command.CommandText.Contains("textbook_audio_track", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); } return result; }
    }
    private sealed class CancelAfterSave(CancellationTokenSource cts) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) { cts.Cancel(); return ValueTask.FromResult(result); }
    }
    private sealed class ThrowOnSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected write failure.");
    }
    private sealed class ThrowOnCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected commit failure.");
    }
}
