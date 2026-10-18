using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class TextbookAudioUploadRepositoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const long Day = 86_400_000;
    internal const string Version = "11111111-1111-1111-1111-111111111111";
    internal static TextbookAudioUploadRequest Request() => new(Guid.NewGuid().ToString(), AudioTestDatabase.Hash,
        "book", "unit", "track", Version, 123.5, AudioTestDatabase.Hash, 128, "test-audio");
    internal static async Task InitAsync(VocabularyDbContext db)
    {
        await db.Database.MigrateAsync(Token);
        await AudioTestDatabase.SeedAsync(db);
        db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "book", UnitId = "unit", Title = "Original", DisplayOrder = 7, Version = Version });
        db.VocabularyBooks.Add(new() { Id = "second-book", BookName = "Second" });
        db.VocabularyBookUnits.Add(new() { Id = "second-unit", BookId = "second-book", Number = 1 });
        db.TextbookAudioTracks.Add(new() { Id = "second-track", BookId = "second-book", UnitId = "second-unit", Title = "Other", Version = Version });
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
    }
    private static TextbookAudioUploadRepository Repo(VocabularyDbContext db, Clock? clock = null) => new(db, clock ?? new Clock());
    private static async Task<TextbookAudioStoredUpload> ReserveAsync(TextbookAudioUploadRepository repo, TextbookAudioUploadRequest? request = null)
    {
        var result = await repo.ReserveAsync(request ?? Request(), "uploader", 1, 300_001, Token);
        Assert.Equal(AudioReservationDisposition.Created, result.Disposition);
        return Assert.IsType<TextbookAudioStoredUpload>(result.Upload);
    }
    private static async Task<TextbookAudioUploadOperation> PublishAsync(TextbookAudioUploadRepository repo, TextbookAudioStoredUpload upload, long now = 3)
    {
        Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, now - 1, Token));
        return Assert.IsType<TextbookAudioUploadOperation>(await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, now, Token));
    }
    private static async Task<TextbookAudioTrackEntity> TrackAsync(VocabularyDbContext db) => await db.TextbookAudioTracks.AsNoTracking().SingleAsync(x => x.Id == "track", Token);

    [Fact]
    public async Task PublishAndPermanentReplay_PreserveMetadataAndHistoryAfterReplacementAndParentDeletion()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = Repo(db); var request = Request(); var first = await ReserveAsync(repo, request);
        Assert.Matches("^audio/[0-9a-f]{32}\\.mp3$", first.Asset.Location.Key);
        Assert.Null(await repo.CompleteAsync(request.IdempotencyKey, first.Operation.UploadLease, 2, Token));
        Assert.Equal(AudioReservationDisposition.InProgress, (await repo.ReserveAsync(request, "other", 2, 100, Token)).Disposition);
        Assert.Equal(AudioReservationDisposition.Conflict, (await repo.ReserveAsync(request with { RequestFingerprint = new string('a', 64) }, "other", 2, 100, Token)).Disposition);
        var success = await PublishAsync(repo, first); var track = await TrackAsync(db);
        Assert.Equal(200, success.Receipt!.StatusCode); Assert.Equal("track", success.Receipt.TrackId);
        Assert.Equal(track.Version, success.Receipt.Version); Assert.NotEqual(Version, track.Version);
        Assert.Equal(first.Asset.Id, track.CurrentAssetId); Assert.Equal(123.5, track.DurationSeconds);
        Assert.Equal(123.5, success.Receipt.DurationSeconds); Assert.False(track.IsAvailable);
        Assert.Equal("Original", track.Title); Assert.Equal(7, track.DisplayOrder);
        var replacing = await ReserveAsync(repo, Request() with { ExpectedVersion = track.Version, DurationSeconds = 80 });
        var second = await PublishAsync(repo, replacing, 5);
        Assert.Equal(200, second.Receipt!.StatusCode); Assert.Equal(80, (await TrackAsync(db)).DurationSeconds);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM textbook_audio_track; DELETE FROM vocabulary_meaning_unit; DELETE FROM vocabulary_meaning; DELETE FROM vocabulary; DELETE FROM vocabulary_book_unit; DELETE FROM vocabulary_book", Token);
        await using var restart = file.Open(); var restarted = Repo(restart);
        Assert.Equal(success.Receipt, (await restarted.ReserveAsync(request, "retry", 6, 100, Token)).Upload!.Operation.Receipt);
        Assert.Equal(2, await restart.AudioAssets.CountAsync(Token));
        Assert.Equal(2, await restart.TextbookAudioUploadOperations.CountAsync(Token));
        Assert.Null(await restarted.ClaimCleanupAsync(request.IdempotencyKey, "cleaner", Day + 10, Day + 100, Token));
        Assert.Null(await restarted.ClaimCleanupAsync(replacing.Operation.IdempotencyKey, "cleaner", Day + 10, Day + 100, Token));
        Assert.Empty(await restarted.FindRecoverableAsync(Day + 10, 100, Token));
        Assert.All(await restart.AudioAssets.ToListAsync(Token), asset => Assert.NotNull(asset.FirstPublishedAtUnixMs));
    }

    [Theory]
    [InlineData("edit", 409)]
    [InlineData("availability", 409)]
    [InlineData("delete", 404)]
    [InlineData("parents", 404)]
    public async Task TransferInterval_AllowsDirectoryWritesButRejectsChangedTarget(string change, int status)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = Repo(db);
        var original = await ReserveAsync(repo); await PublishAsync(repo, original); var before = await TrackAsync(db);
        var request = Request() with { ExpectedVersion = before.Version }; var upload = await ReserveAsync(repo, request);
        Assert.True(await repo.BeginTransferAsync(request.IdempotencyKey, upload.Operation.UploadLease, 4, Token));
        await using var other = file.Open();
        await new UnitOfWork(other).ExecuteInTransactionAsync(async () =>
        {
            if (change is "delete" or "parents") await other.TextbookAudioTracks.Where(x => x.Id == "track").ExecuteDeleteAsync(Token);
            if (change == "parents") await other.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning_unit; DELETE FROM vocabulary_meaning WHERE book_id='book'; DELETE FROM vocabulary_book_unit WHERE id='unit'; DELETE FROM vocabulary_book WHERE id='book'", Token);
            if (change is "edit" or "availability") await new TextbookAudioTrackRepository(other).ReplaceAsync(
                new("track", "book", "unit", change == "edit" ? "Edited" : before.Title, before.DisplayOrder, before.CurrentAssetId, before.DurationSeconds,
                    change == "availability", Guid.NewGuid().ToString()), before.Version, Token);
            return 0;
        });
        var terminal = Assert.IsType<TextbookAudioUploadOperation>(await repo.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, 5, Token));
        Assert.Equal(status, terminal.Receipt!.StatusCode); Assert.Null(terminal.Receipt.Version); Assert.Null(terminal.Receipt.DurationSeconds);
        Assert.Null((await repo.GetAsync(request.IdempotencyKey, Token))!.Asset.FirstPublishedAtUnixMs);
        if (change is "edit" or "availability")
        {
            var retained = await TrackAsync(db); Assert.Equal(before.CurrentAssetId, retained.CurrentAssetId); Assert.Equal(before.DurationSeconds, retained.DurationSeconds);
            Assert.Equal(change == "edit" ? "Edited" : "Original", retained.Title); Assert.Equal(change == "availability", retained.IsAvailable);
        }
        Assert.Equal(terminal.Receipt, (await repo.ReserveAsync(request, "retry", 6, 100, Token)).Upload!.Operation.Receipt);
        Assert.NotNull((await repo.GetAsync(original.Operation.IdempotencyKey, Token))!.Asset.FirstPublishedAtUnixMs);
    }

    [Fact]
    public async Task SeparateConnections_ConcurrentCasHasExactlyOneWinner()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = Repo(db);
        var first = await ReserveAsync(repo); var second = await ReserveAsync(repo);
        await repo.BeginTransferAsync(first.Operation.IdempotencyKey, first.Operation.UploadLease, 2, Token);
        await repo.BeginTransferAsync(second.Operation.IdempotencyKey, second.Operation.UploadLease, 2, Token);
        await using var other = file.Open();
        var results = await Task.WhenAll(repo.CompleteAsync(first.Operation.IdempotencyKey, first.Operation.UploadLease, 3, Token),
            Repo(other).CompleteAsync(second.Operation.IdempotencyKey, second.Operation.UploadLease, 3, Token));
        var success = Assert.Single(results, x => x!.Receipt!.StatusCode == 200)!;
        Assert.Single(results, x => x!.Receipt!.StatusCode == 409);
        Assert.Equal(success.AssetId, (await TrackAsync(db)).CurrentAssetId);
        Assert.Single(await db.AudioAssets.Where(x => x.FirstPublishedAtUnixMs != null).ToListAsync(Token));
    }

    [Fact]
    public async Task LeaseGuardsAndCleanup_KeepOriginalReceiptAndTombstoneForLatePut()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = Repo(db);
        var request = Request(); var upload = await ReserveAsync(repo, request); var key = request.IdempotencyKey; var lease = upload.Operation.UploadLease;
        foreach (var wrong in new[] { lease with { Owner = "wrong" }, lease with { Owner = null }, lease with { Fence = 2 } })
        {
            Assert.False(await repo.BeginTransferAsync(key, wrong, 2, Token));
            Assert.False(await repo.RenewAsync(key, wrong, 2, 400_001, Token));
            Assert.Null(await repo.CompleteAsync(key, wrong, 2, Token));
            Assert.False(await repo.AbandonAsync(key, wrong, 2, 503, "audio.upload_failed", Token));
        }
        Assert.True(await repo.RenewAsync(key, lease, 2, 400_001, Token));
        Assert.False(await repo.RenewAsync(key, lease, 3, 100, Token));
        Assert.True(await repo.BeginTransferAsync(key, lease, 3, Token));
        Assert.False(await repo.BeginTransferAsync(key, lease, 4, Token));
        Assert.Null(await repo.ClaimCleanupAsync(key, "cleaner", Day + 1, Day + 100, Token));
        Assert.False(await repo.ExpireAsync(key, 400_000, Token));
        Assert.Null(await repo.CompleteAsync(key, lease, 400_001, Token));
        Assert.False(await repo.AbandonAsync(key, lease, 400_001, 503, "audio.upload_failed", Token));
        Assert.True(await repo.ExpireAsync(key, 400_001, Token));
        var receipt = (await repo.GetAsync(key, Token))!.Operation.Receipt; Assert.Equal(503, receipt!.StatusCode);
        Assert.Null(await repo.ClaimCleanupAsync(key, "cleaner", Day, Day + 100, Token));
        await using var other = file.Open(); var otherRepo = Repo(other);
        var claims = await Task.WhenAll(repo.ClaimCleanupAsync(key, "a", Day + 1, Day + 100, Token), otherRepo.ClaimCleanupAsync(key, "b", Day + 1, Day + 100, Token));
        var claim = Assert.Single(claims, x => x != null)!; var cleanup = claim.Operation.Cleanup.Lease;
        Assert.Null(await repo.CompleteAsync(key, lease, Day + 2, Token));
        Assert.False(await repo.ObserveCleanupAsync(key, cleanup with { Fence = 99 }, Day + 2, Day + 200, Token));
        Assert.True(await repo.ReleaseCleanupAsync(key, cleanup, Day + 2, Day + 200, "audio.cleanup_failed", Token));
        Assert.Null(await repo.ClaimCleanupAsync(key, "c", Day + 199, Day + 300, Token));
        claim = (await repo.ClaimCleanupAsync(key, "c", Day + 200, Day + 300, Token))!;
        Assert.Equal(cleanup.Fence + 1, claim.Operation.Cleanup.Lease.Fence);
        Assert.False(await repo.ObserveCleanupAsync(key, cleanup, Day + 201, Day + 400, Token));
        Assert.True(await repo.ObserveCleanupAsync(key, claim.Operation.Cleanup.Lease, Day + 201, Day + 400, Token));
        var tombstone = (await repo.GetAsync(key, Token))!; Assert.Equal(AudioUploadState.CleanupObserved, tombstone.Operation.State);
        Assert.Equal(upload.Asset.Location, tombstone.Asset.Location); Assert.Equal(receipt, tombstone.Operation.Receipt);
        Assert.Equal(receipt, (await repo.ReserveAsync(request, "retry", Day + 202, Day + 300, Token)).Upload!.Operation.Receipt);
        Assert.Empty(await repo.FindRecoverableAsync(Day + 399, 100, Token));
        Assert.Single(await repo.FindRecoverableAsync(Day + 400, 1, Token));
        var lateClaim = (await repo.ClaimCleanupAsync(key, "late", Day + 400, Day + 500, Token))!;
        Assert.False(await repo.ExpireCleanupAsync(key, Day + 499, Day + 600, Token));
        Assert.False(await repo.ObserveCleanupAsync(key, lateClaim.Operation.Cleanup.Lease, Day + 500, Day + 600, Token));
        Assert.True(await repo.ExpireCleanupAsync(key, Day + 500, Day + 600, Token));
        Assert.Equal(AudioUploadState.CleanupObserved, (await repo.GetAsync(key, Token))!.Operation.State);
        Assert.Equal(receipt, (await repo.GetAsync(key, Token))!.Operation.Receipt);
    }

    [Fact]
    public async Task PublishedStampProtectsEvenFailedAndUnreferencedAsset()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = Repo(db); var upload = await ReserveAsync(repo);
        Assert.True(await repo.AbandonAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, 503, "audio.upload_failed", Token));
        await db.AudioAssets.Where(x => x.Id == upload.Asset.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FirstPublishedAtUnixMs, 3), Token);
        Assert.Null(await repo.ClaimCleanupAsync(upload.Operation.IdempotencyKey, "cleaner", Day + 1, Day + 100, Token));
        Assert.Empty(await repo.FindRecoverableAsync(Day + 1, 100, Token));
    }

    [Fact]
    public async Task WriterAdmission_RechecksClockAndCancelsQueueWithoutHoldingLockAcrossTransfer()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var clock = new Clock(); var repo = Repo(db, clock); var upload = await ReserveAsync(repo);
        Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        await using var other = file.Open(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UnitOfWork(other).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        using var cancelled = new CancellationTokenSource();
        var queuedReserve = repo.ReserveAsync(Request(), "owner", 3, 100, cancelled.Token);
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedReserve);
        var queuedPublish = repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 3, Token);
        clock.Now = 300_001; release.SetResult(); await holder;
        Assert.Null(await queuedPublish); Assert.Null((await TrackAsync(db)).CurrentAssetId);
        Assert.Single(await db.TextbookAudioUploadOperations.ToListAsync(Token));
        Assert.Single(await repo.FindRecoverableAsync(clock.Now, 100, Token));
        Assert.True(await repo.ExpireAsync(upload.Operation.IdempotencyKey, 3, Token));
        Assert.Equal(503, (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Operation.Receipt!.StatusCode);
    }

    [Fact]
    public async Task CancelAfterAdmission_CompletesShortCommit_AndOuterTransactionsAreRejected()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        using var cancel = new CancellationTokenSource(); await using var other = file.Open(new CancelOnAssetInsert(cancel));
        var repo = Repo(other); var request = Request();
        Assert.Equal(AudioReservationDisposition.Created, (await repo.ReserveAsync(request, "owner", 1, 100, cancel.Token)).Disposition);
        Assert.True(cancel.IsCancellationRequested); Assert.Single(await db.TextbookAudioUploadOperations.ToListAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(() => Repo(db).ReserveAsync(Request(), "owner", 1, 100, Token)));
        await using var transaction = await db.Database.BeginTransactionAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo(db).ReserveAsync(Request(), "owner", 1, 100, Token));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("receipt")]
    [InlineData("commit")]
    public async Task InjectedFailure_RollsBackAssetDirectoryPublicationAndReceipt(string phase)
    {
        using var file = new AudioTestDatabase(); await using (var seed = file.Open()) await InitAsync(seed);
        var fail = new FailCommand(); var commit = new FailCommit(); await using var db = file.Open(fail, commit); var repo = Repo(db);
        if (phase == "save")
        {
            fail.Match = "INSERT INTO \"textbook_audio_upload_operation\"";
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => ReserveAsync(repo));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Empty(await db.AudioAssets.ToListAsync(Token)); Assert.Empty(await db.TextbookAudioUploadOperations.ToListAsync(Token)); return;
        }
        var original = await ReserveAsync(repo); await PublishAsync(repo, original); var before = await TrackAsync(db);
        var upload = await ReserveAsync(repo, Request() with { ExpectedVersion = before.Version });
        Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 4, Token));
        if (phase == "receipt") fail.Match = "UPDATE \"textbook_audio_upload_operation\"";
        if (phase == "commit") commit.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 5, Token));
        var after = await TrackAsync(db); Assert.Equal(before.CurrentAssetId, after.CurrentAssetId); Assert.Equal(before.Version, after.Version); Assert.Equal(before.DurationSeconds, after.DurationSeconds);
        var retained = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!;
        Assert.Null(retained.Asset.FirstPublishedAtUnixMs); Assert.Null(retained.Operation.Receipt); Assert.Equal(AudioUploadState.Transferring, retained.Operation.State);
    }

    [Fact]
    public async Task ReserveValidatesOwnershipOpaqueIdsFingerprintAndInputs_WithoutAllocating()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = Repo(db);
        foreach (var request in new[] { Request() with { BookId = "missing" }, Request() with { UnitId = "missing" }, Request() with { TrackId = "missing" } })
            Assert.Equal(AudioReservationDisposition.NotFound, (await repo.ReserveAsync(request, "owner", 1, 100, Token)).Disposition);
        foreach (var request in new[] { Request() with { UnitId = "second-unit" }, Request() with { TrackId = "second-track" }, Request() with { ExpectedVersion = Guid.NewGuid().ToString() } })
            Assert.Equal(AudioReservationDisposition.Conflict, (await repo.ReserveAsync(request, "owner", 1, 100, Token)).Disposition);
        foreach (var request in new[] { Request() with { RequestFingerprint = "wrong" }, Request() with { Sha256 = new string('A', 64) }, Request() with { ByteLength = 0 }, Request() with { DurationSeconds = double.NaN }, Request() with { DurationSeconds = double.PositiveInfinity }, Request() with { DurationSeconds = 0 }, Request() with { ExpectedVersion = "wrong" }, Request() with { IdempotencyKey = "wrong" }, Request() with { TrackId = " track " } })
            await Assert.ThrowsAsync<ArgumentException>(() => repo.ReserveAsync(request, "owner", 1, 100, Token));
        foreach (var prefix in new[] { "../", "audio/../", "/audio/", "audio//", "Audio/", "a/b/c/d/e/", "audio" })
            await Assert.ThrowsAsync<ArgumentException>(() => repo.ReserveAsync(Request() with { KeyPrefix = prefix }, "owner", 1, 100, Token));
        Assert.Empty(await db.AudioAssets.ToListAsync(Token));
        var upload = await ReserveAsync(repo, Request() with { KeyPrefix = "private/textbooks/" });
        Assert.Matches("^private/textbooks/[0-9a-f]{32}\\.mp3$", upload.Asset.Location.Key);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.AbandonAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, 503, "unsafe-value", Token));
        // The same key is independently scoped from the existing vocabulary protocol.
        db.AudioAssets.Add(AudioTestDatabase.Asset("vocabulary"));
        var old = AudioTestDatabase.Operation("vocabulary", AudioUploadState.Succeeded); old.IdempotencyKey = upload.Operation.IdempotencyKey;
        db.AudioUploadOperations.Add(old); await db.SaveChangesAsync(Token);
        Assert.Equal(upload.Operation, (await repo.GetAsync(old.IdempotencyKey, Token))!.Operation);
    }

    [Fact]
    public async Task ActualSqliteWriterReservation_BlocksExternalConnectionDuringTargetRead()
    {
        using var file = new AudioTestDatabase(); await using (var seed = file.Open()) await InitAsync(seed);
        var external = new ExternalWrite(file); await using var db = file.Open(external); var repo = Repo(db); var upload = await ReserveAsync(repo);
        external.Enabled = true; await PublishAsync(repo, upload); Assert.True(external.Blocked);
    }

    private sealed class Clock : TimeProvider
    {
        public long Now { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    private sealed class CancelOnAssetInsert(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO \"audio_asset\"", StringComparison.Ordinal)) source.Cancel();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailCommand : DbCommandInterceptor
    {
        public string? Match { get; set; }
        private void Check(DbCommand command) { if (Match != null && command.CommandText.StartsWith(Match, StringComparison.Ordinal)) throw new InvalidOperationException("Injected write failure."); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
    }
    private sealed class FailCommit : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new InvalidOperationException("Injected commit failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ExternalWrite(AudioTestDatabase file) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool Blocked { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !Blocked && command.CommandText.Contains("FROM \"textbook_audio_track\"", StringComparison.Ordinal))
            {
                await using var connection = new SqliteConnection($"Data Source={file.PathName};Pooling=False;Default Timeout=1"); await connection.OpenAsync(cancellationToken);
                await using var change = connection.CreateCommand(); change.CommandTimeout = 1;
                change.CommandText = "UPDATE textbook_audio_track SET version='22222222-2222-2222-2222-222222222222' WHERE id='track'";
                try { await change.ExecuteNonQueryAsync(cancellationToken); }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 5) { Blocked = true; }
            }
            return result;
        }
    }
}
