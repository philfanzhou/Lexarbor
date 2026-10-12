using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioUploadRepositoryTests
{
    private static CancellationToken Token => AudioTestDatabase.Token;
    private const long Day = 86_400_000;
    private static AudioUploadRequest Request(string? meaning = null, AudioLocale locale = AudioLocale.EnGb, string? expected = null) =>
        new(Guid.NewGuid().ToString(), AudioTestDatabase.Hash, "word", meaning, locale, AudioTestDatabase.Revision(locale, meaning != null),
            expected, AudioTestDatabase.Hash, 128, "test-audio");
    private static async Task InitAsync(VocabularyDbContext db)
    {
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
    }
    private static async Task<AudioStoredUpload> ReserveAsync(AudioUploadRepository repo, AudioUploadRequest request)
    {
        var result = await repo.ReserveAsync(request, "uploader", 1, 300_001, Token);
        Assert.Equal(AudioReservationDisposition.Created, result.Disposition);
        return Assert.IsType<AudioStoredUpload>(result.Upload);
    }
    private static async Task<AudioUploadOperation> PublishAsync(AudioUploadRepository repo, AudioStoredUpload upload)
    {
        Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        return Assert.IsType<AudioUploadOperation>(await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 3, Token));
    }

    [Theory]
    [InlineData(null, AudioLocale.EnGb)]
    [InlineData(null, AudioLocale.EnUs)]
    [InlineData("meaning", AudioLocale.EnGb)]
    [InlineData("meaning", AudioLocale.EnUs)]
    public async Task IdempotencyAndSuccess_NeverPublishTwice(string? meaning, AudioLocale locale)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var request = Request(meaning, locale);
        var upload = await ReserveAsync(repo, request);
        Assert.Matches("^audio/[0-9a-f]{32}\\.mp3$", upload.Asset.Location.Key);
        Assert.Null(upload.Operation.Receipt); Assert.Null(upload.Asset.FirstPublishedAtUnixMs);
        Assert.Equal(AudioReservationDisposition.InProgress, (await repo.ReserveAsync(request, "other", 2, 100, Token)).Disposition);
        Assert.Equal(AudioReservationDisposition.Conflict, (await repo.ReserveAsync(request with { RequestFingerprint = new string('a', 64) }, "other", 2, 100, Token)).Disposition);
        Assert.Null(await repo.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        var success = await PublishAsync(repo, upload); Assert.Equal(AudioUploadState.Succeeded, success.State);
        Assert.Equal(200, success.Receipt!.StatusCode); Assert.Equal(upload.Asset.Id, success.Receipt.AssetId);
        Assert.False(await repo.BeginTransferAsync(request.IdempotencyKey, upload.Operation.UploadLease, 4, Token));
        Assert.False(await repo.RenewAsync(request.IdempotencyKey, upload.Operation.UploadLease, 4, 200, Token));
        Assert.False(await repo.AbandonAsync(request.IdempotencyKey, upload.Operation.UploadLease, 4, 503, "audio.failed", Token));
        Assert.Null(await repo.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, 4, Token));
        Assert.Null(await repo.ClaimCleanupAsync(request.IdempotencyKey, "cleaner", Day + 4, Day + 100, Token));
        Assert.False(await repo.ExpireAsync(request.IdempotencyKey, Day + 4, Token));
        var replay = await repo.ReserveAsync(request, "other", 4, 200, Token);
        Assert.Equal(AudioReservationDisposition.Replay, replay.Disposition); Assert.Equal(success.Receipt, replay.Upload!.Operation.Receipt);
        Assert.Equal(3, replay.Upload.Asset.FirstPublishedAtUnixMs);
        Assert.Equal(AudioUnbindDisposition.Removed, await repo.UnbindAsync("word", meaning, locale, upload.Asset.Id, Token));
        Assert.Equal(0, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Equal(AudioUnbindDisposition.NotFound, await repo.UnbindAsync("word", meaning, locale, upload.Asset.Id, Token));
        Assert.Null(await repo.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, 5, Token));
        Assert.Equal(success.Receipt, (await repo.GetAsync(request.IdempotencyKey, Token))!.Operation.Receipt);
    }

    [Fact]
    public async Task LeaseAndTransitionGuards_EnforceOwnerFenceExpiryAndImmutableFailure()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var upload = await ReserveAsync(repo, Request()); var key = upload.Operation.IdempotencyKey;
        var lease = upload.Operation.UploadLease;
        foreach (var wrong in new[] { lease with { Owner = "other" }, lease with { Fence = 2 }, lease with { Owner = null } })
        {
            Assert.False(await repo.BeginTransferAsync(key, wrong, 2, Token));
            Assert.False(await repo.RenewAsync(key, wrong, 2, 400_001, Token));
            Assert.False(await repo.AbandonAsync(key, wrong, 2, 503, "audio.failed", Token));
        }
        Assert.True(await repo.RenewAsync(key, lease, 2, 400_001, Token));
        Assert.False(await repo.RenewAsync(key, lease, 3, 100, Token));
        Assert.True(await repo.BeginTransferAsync(key, lease, 3, Token));
        Assert.False(await repo.BeginTransferAsync(key, lease, 4, Token));
        Assert.True(await repo.RenewAsync(key, lease, 4, 500_001, Token));
        Assert.False(await repo.ExpireAsync(key, 500_000, Token));
        Assert.Null(await repo.CompleteAsync(key, lease, 500_001, Token));
        Assert.False(await repo.AbandonAsync(key, lease, 500_001, 503, "audio.failed", Token));
        Assert.True(await repo.ExpireAsync(key, 500_001, Token));
        var failed = (await repo.GetAsync(key, Token))!.Operation;
        Assert.Equal(503, failed.Receipt!.StatusCode); Assert.Equal("audio.upload_expired", failed.Receipt.ErrorCode);
        Assert.False(await repo.ExpireAsync(key, 500_002, Token));
        Assert.False(await repo.BeginTransferAsync(key, lease, 500_002, Token));
        Assert.Equal(failed.Receipt, (await repo.ReserveAsync(Request() with { IdempotencyKey = key }, "other", 500_002, 600_000, Token)).Upload!.Operation.Receipt);
        Assert.Equal(0, await db.VocabularyAudioBindings.CountAsync(Token));
    }

    [Theory]
    [InlineData(AudioUploadState.Reserved, 404)]
    [InlineData(AudioUploadState.Reserved, 409)]
    [InlineData(AudioUploadState.Transferring, 503)]
    public async Task Abandon_PreservesReceiptAndKnownObject(AudioUploadState state, int status)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var request = Request(); var upload = await ReserveAsync(repo, request);
        if (state == AudioUploadState.Transferring) Assert.True(await repo.BeginTransferAsync(request.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        Assert.True(await repo.AbandonAsync(request.IdempotencyKey, upload.Operation.UploadLease, 3, status, "audio.failed", Token));
        Assert.False(await repo.AbandonAsync(request.IdempotencyKey, upload.Operation.UploadLease, 4, 503, "audio.other", Token));
        var replay = await repo.ReserveAsync(request, "other", 4, 100, Token);
        Assert.Equal(AudioReservationDisposition.Replay, replay.Disposition); Assert.Equal(status, replay.Upload!.Operation.Receipt!.StatusCode);
        Assert.Equal(upload.Asset.Location, replay.Upload.Asset.Location); Assert.Equal(1, await db.AudioAssets.CountAsync(Token));
    }

    [Theory]
    [InlineData("edit", 409)]
    [InlineData("import", 409)]
    [InlineData("meaning-edit", 409)]
    [InlineData("word-delete", 404)]
    [InlineData("meaning-delete", 404)]
    public async Task FinalPublish_RechecksRealEditsImportsAndTargetDeletion(string change, int status)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var request = Request(change.StartsWith("meaning", StringComparison.Ordinal) ? "meaning" : null);
        var upload = await ReserveAsync(repo, request); Assert.True(await repo.BeginTransferAsync(request.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        var uow = new UnitOfWork(db);
        if (change == "edit")
            await new VocabularyWordEditService(new VocabularyRepository(db), new VocabularyWordEditRepository(db), new VocabularyMeaningRepository(db),
                new VocabularyMeaningUnitRepository(db), uow).ReplaceAsync("word", "APPLE", "uk", "us", Token);
        if (change == "import")
            await new VocabularyDomainService(new VocabularyRepository(db), new VocabularyBookRepository(db), new VocabularyMeaningRepository(db),
                new VocabularyBookUnitRepository(db), new VocabularyMeaningUnitRepository(db), uow)
                .ImportBatchAsync("book", [(new() { Word = "Apple", PhoneticUk = "changed" }, new() { PartOfSpeech = "n.", Meaning = "fruit" }, null, null, null)]);
        if (change == "meaning-edit")
            await new VocabularyMeaningEditService(new VocabularyRepository(db), new VocabularyBookRepository(db), new VocabularyMeaningRepository(db),
                new VocabularyMeaningUnitRepository(db), uow).ReplaceAsync("book", "word", "meaning", "v.", "changed", null, Token);
        if (change == "word-delete") await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='word'", Token);
        if (change == "meaning-delete") await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning WHERE id='meaning'", Token);
        var result = Assert.IsType<AudioUploadOperation>(await repo.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, 3, Token));
        Assert.Equal(AudioUploadState.Abandoned, result.State); Assert.Equal(status, result.Receipt!.StatusCode);
        Assert.Equal(0, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Null((await repo.GetAsync(request.IdempotencyKey, Token))!.Asset.FirstPublishedAtUnixMs);
    }

    [Fact]
    public async Task ConcurrentReplacements_OneWinnerAndFailurePreservesOldBinding()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var original = await ReserveAsync(repo, Request()); await PublishAsync(repo, original);
        var first = await ReserveAsync(repo, Request(expected: original.Asset.Id)); var second = await ReserveAsync(repo, Request(expected: original.Asset.Id));
        Assert.True(await repo.BeginTransferAsync(first.Operation.IdempotencyKey, first.Operation.UploadLease, 4, Token));
        Assert.True(await repo.BeginTransferAsync(second.Operation.IdempotencyKey, second.Operation.UploadLease, 4, Token));
        await using var otherDb = file.Open(); var other = new AudioUploadRepository(otherDb, new ManualClock());
        var results = await Task.WhenAll(repo.CompleteAsync(first.Operation.IdempotencyKey, first.Operation.UploadLease, 5, Token),
            other.CompleteAsync(second.Operation.IdempotencyKey, second.Operation.UploadLease, 5, Token));
        Assert.Single(results, op => op!.State == AudioUploadState.Succeeded); Assert.Single(results, op => op!.Receipt!.StatusCode == 409);
        var binding = await db.VocabularyAudioBindings.AsNoTracking().SingleAsync(Token);
        Assert.Equal(results.Single(op => op!.State == AudioUploadState.Succeeded)!.AssetId, binding.AssetId);
        var abandoned = await ReserveAsync(repo, Request(expected: binding.AssetId));
        Assert.True(await repo.AbandonAsync(abandoned.Operation.IdempotencyKey, abandoned.Operation.UploadLease, 6, 503, "audio.failed", Token));
        Assert.Equal(binding.AssetId, (await db.VocabularyAudioBindings.AsNoTracking().SingleAsync(Token)).AssetId);
        Assert.All(await db.AudioAssets.AsNoTracking().Where(asset => asset.Id == original.Asset.Id || asset.Id == binding.AssetId).ToListAsync(Token), asset => Assert.NotNull(asset.FirstPublishedAtUnixMs));
    }

    [Fact]
    public async Task Cleanup_EnforcesDelayOwnershipRetryAndRetainsReceiptForLatePut()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var upload = await ReserveAsync(repo, Request()); var key = upload.Operation.IdempotencyKey;
        Assert.Null(await repo.ClaimCleanupAsync(key, "cleaner", Day + 1, Day + 100, Token));
        Assert.True(await repo.ExpireAsync(key, 300_001, Token)); var receipt = (await repo.GetAsync(key, Token))!.Operation.Receipt;
        Assert.Null(await repo.ClaimCleanupAsync(key, "cleaner", Day, Day + 100, Token));
        var claim = Assert.IsType<AudioStoredUpload>(await repo.ClaimCleanupAsync(key, "cleaner", Day + 1, Day + 100, Token));
        var lease = claim.Operation.Cleanup.Lease;
        Assert.False(await repo.ObserveCleanupAsync(key, lease with { Fence = 2 }, Day + 2, Day + 200, Token));
        Assert.Null(await repo.CompleteAsync(key, upload.Operation.UploadLease, Day + 2, Token));
        Assert.Null(await repo.ClaimCleanupAsync(key, "other", Day + 2, Day + 200, Token));
        Assert.True(await repo.ReleaseCleanupAsync(key, lease, Day + 2, Day + 200, "audio.delete_failed", Token));
        Assert.Null(await repo.ClaimCleanupAsync(key, "cleaner", Day + 199, Day + 300, Token));
        var retry = Assert.IsType<AudioStoredUpload>(await repo.ClaimCleanupAsync(key, "cleaner", Day + 200, Day + 300, Token));
        Assert.Equal(2, retry.Operation.Cleanup.Lease.Fence);
        Assert.False(await repo.ObserveCleanupAsync(key, lease, Day + 201, Day + 400, Token));
        Assert.True(await repo.ObserveCleanupAsync(key, retry.Operation.Cleanup.Lease, Day + 201, Day + 400, Token));
        var observed = (await repo.GetAsync(key, Token))!; Assert.Equal(receipt, observed.Operation.Receipt);
        Assert.Equal(AudioUploadState.CleanupObserved, observed.Operation.State); Assert.Equal(upload.Asset.Location, observed.Asset.Location);
        var recheck = Assert.IsType<AudioStoredUpload>(await repo.ClaimCleanupAsync(key, "cleaner", Day + 400, Day + 500, Token));
        Assert.False(await repo.ExpireCleanupAsync(key, Day + 499, Day + 600, Token));
        Assert.False(await repo.ObserveCleanupAsync(key, recheck.Operation.Cleanup.Lease, Day + 500, Day + 600, Token));
        Assert.True(await repo.ExpireCleanupAsync(key, Day + 500, Day + 600, Token));
        Assert.Equal(AudioUploadState.CleanupObserved, (await repo.GetAsync(key, Token))!.Operation.State);
        Assert.Equal(receipt, (await repo.GetAsync(key, Token))!.Operation.Receipt);
        Assert.Equal(3, (await repo.GetAsync(key, Token))!.Operation.Cleanup.Attempts);
        Assert.Empty(await repo.FindRecoverableAsync(Day + 599, 100, Token));
        Assert.Single(await repo.FindRecoverableAsync(Day + 600, 100, Token));
    }

    [Fact]
    public async Task ConcurrentReserveAndCleanup_HaveOneOwner()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        await using var otherDb = file.Open(); var first = new AudioUploadRepository(db, new ManualClock()); var second = new AudioUploadRepository(otherDb, new ManualClock()); var request = Request();
        var reserves = await Task.WhenAll(first.ReserveAsync(request, "first", 1, 100, Token), second.ReserveAsync(request, "second", 1, 100, Token));
        Assert.Single(reserves, result => result.Disposition == AudioReservationDisposition.Created);
        Assert.Single(reserves, result => result.Disposition == AudioReservationDisposition.InProgress);
        Assert.True(await first.ExpireAsync(request.IdempotencyKey, 100, Token));
        var claims = await Task.WhenAll(first.ClaimCleanupAsync(request.IdempotencyKey, "first", Day + 1, Day + 100, Token),
            second.ClaimCleanupAsync(request.IdempotencyKey, "second", Day + 1, Day + 100, Token));
        Assert.Single(claims, claim => claim != null); Assert.Equal(1, await db.AudioAssets.CountAsync(Token));
    }

    [Fact]
    public async Task ConditionalUnbind_OnlyTouchesOwnTargetAndRetainsAssetsAndOperations()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = new AudioUploadRepository(db, new ManualClock());
        var published = new List<AudioStoredUpload>();
        foreach (var locale in Enum.GetValues<AudioLocale>()) foreach (var meaning in new string?[] { null, "meaning" })
        { var upload = await ReserveAsync(repo, Request(meaning, locale)); await PublishAsync(repo, upload); published.Add(upload); }
        var gbMeaning = published.Single(op => op.Operation.Locale == AudioLocale.EnGb && op.Operation.MeaningId != null);
        Assert.Equal(AudioUnbindDisposition.Conflict, await repo.UnbindAsync("word", "meaning", AudioLocale.EnGb, "wrong", Token));
        Assert.Equal(AudioUnbindDisposition.NotFound, await repo.UnbindAsync("word", "other-meaning", AudioLocale.EnGb, gbMeaning.Asset.Id, Token));
        Assert.Equal(AudioUnbindDisposition.Removed, await repo.UnbindAsync("word", "meaning", AudioLocale.EnGb, gbMeaning.Asset.Id, Token));
        Assert.Equal(3, await db.VocabularyAudioBindings.CountAsync(Token));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id='word'", Token);
        Assert.Equal(0, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Equal(4, await db.AudioAssets.CountAsync(Token)); Assert.Equal(4, await db.AudioUploadOperations.CountAsync(Token));
    }

    [Fact]
    public async Task FaultDuringSuccess_RollsBackBindingStampAndReceipt()
    {
        using var file = new AudioTestDatabase(); await using (var seed = file.Open()) await InitAsync(seed);
        var fault = new FailSuccessUpdate(); await using var db = file.Open(fault); var repo = new AudioUploadRepository(db, new ManualClock());
        var upload = await ReserveAsync(repo, Request()); Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        fault.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 3, Token));
        var saved = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!;
        Assert.Equal(AudioUploadState.Transferring, saved.Operation.State); Assert.Null(saved.Operation.Receipt); Assert.Null(saved.Asset.FirstPublishedAtUnixMs);
        Assert.Equal(0, await db.VocabularyAudioBindings.CountAsync(Token));
        fault.Enabled = false; Assert.Equal(AudioUploadState.Succeeded, (await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 4, Token))!.State);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionDoesNothing_AfterAdmissionFinishesCommit()
    {
        using var file = new AudioTestDatabase(); await using (var seed = file.Open()) await InitAsync(seed);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await using var db = file.Open(); var repo = new AudioUploadRepository(db, new ManualClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ReserveAsync(Request(), "owner", 1, 100, cancel.Token));
        Assert.Equal(0, await db.AudioAssets.CountAsync(Token));
        using var admitted = new CancellationTokenSource(); var interceptor = new CancelOnInsert(admitted);
        await using var other = file.Open(interceptor);
        var result = await new AudioUploadRepository(other, new ManualClock()).ReserveAsync(Request(), "owner", 1, 100, admitted.Token);
        Assert.True(admitted.IsCancellationRequested); Assert.Equal(AudioReservationDisposition.Created, result.Disposition);
        Assert.Equal(1, await db.AudioUploadOperations.CountAsync(Token));
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNothing_TransportIntervalDoesNotHoldWriteLock()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        await using var other = file.Open(); var repo = new AudioUploadRepository(other, new ManualClock());
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UnitOfWork(db).ExecuteInTransactionAsync(async () => { admitted.SetResult(); await release.Task; return 0; });
        await admitted.Task; using var cancelled = new CancellationTokenSource();
        var queued = repo.ReserveAsync(Request(), "owner", 1, 100, cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); release.SetResult(); await holder;
        Assert.Equal(0, await other.AudioAssets.CountAsync(Token));
        var upload = await ReserveAsync(repo, Request()); Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        // The caller may wait arbitrarily for Put here; independent vocabulary writes still commit.
        await new UnitOfWork(db).ExecuteInTransactionAsync(async () => { await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET phonetic_us='during-put' WHERE id='word'", Token); return 0; });
        Assert.Equal(AudioUploadState.Succeeded, (await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 3, Token))!.State);
    }

    [Fact]
    public async Task PublishAndCleanupRace_NeverGiveBothOwnership()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        await using var secondDb = file.Open(); var uploadRepo = new AudioUploadRepository(db, new ManualClock()); var cleanupRepo = new AudioUploadRepository(secondDb, new ManualClock());
        var upload = await ReserveAsync(uploadRepo, Request()); var key = upload.Operation.IdempotencyKey;
        Assert.True(await uploadRepo.BeginTransferAsync(key, upload.Operation.UploadLease, 2, Token));
        var publishing = uploadRepo.CompleteAsync(key, upload.Operation.UploadLease, 3, Token);
        var cleaning = cleanupRepo.ClaimCleanupAsync(key, "cleaner", 3, 100, Token);
        await Task.WhenAll(publishing, cleaning);
        Assert.Equal(AudioUploadState.Succeeded, (await publishing)!.State); Assert.Null(await cleaning);
        var expired = await ReserveAsync(uploadRepo, Request(locale: AudioLocale.EnUs));
        Assert.True(await uploadRepo.BeginTransferAsync(expired.Operation.IdempotencyKey, expired.Operation.UploadLease, 4, Token));
        Assert.True(await cleanupRepo.ExpireAsync(expired.Operation.IdempotencyKey, Day + 1, Token));
        var late = uploadRepo.CompleteAsync(expired.Operation.IdempotencyKey, expired.Operation.UploadLease, Day + 2, Token);
        var claim = cleanupRepo.ClaimCleanupAsync(expired.Operation.IdempotencyKey, "cleaner", Day + 2, Day + 100, Token);
        await Task.WhenAll(late, claim); Assert.Null(await late); var claimed = Assert.IsType<AudioStoredUpload>(await claim);
        Assert.Null(claimed.Asset.FirstPublishedAtUnixMs);
        Assert.Single(await db.VocabularyAudioBindings.AsNoTracking().ToListAsync(Token));
    }

    [Fact]
    public async Task PublishedStampBlocksCleanupEvenForFailedUnreferencedOperation()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var upload = await ReserveAsync(repo, Request());
        Assert.True(await repo.AbandonAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, 503, "audio.failed", Token));
        // This simulates a retained historical publication marker in restored data.
        await db.AudioAssets.Where(asset => asset.Id == upload.Asset.Id).ExecuteUpdateAsync(set => set.SetProperty(asset => asset.FirstPublishedAtUnixMs, 2), Token);
        Assert.Null(await repo.ClaimCleanupAsync(upload.Operation.IdempotencyKey, "cleaner", Day + 1, Day + 100, Token));
        Assert.Empty(await repo.FindRecoverableAsync(Day + 1, 100, Token));
    }

    [Fact]
    public async Task ReserveRejectsWrongTargetConditionsAndUnsafePrefixWithoutState()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = new AudioUploadRepository(db, new ManualClock());
        foreach (var request in new[] { Request() with { WordId = "missing" }, Request("other-meaning") })
            Assert.Equal(AudioReservationDisposition.NotFound, (await repo.ReserveAsync(request, "owner", 1, 100, Token)).Disposition);
        foreach (var request in new[] { Request() with { ExpectedAssetId = "wrong" }, Request() with { SourceRevision = new string('a', 64) } })
            Assert.Equal(AudioReservationDisposition.Conflict, (await repo.ReserveAsync(request, "owner", 1, 100, Token)).Disposition);
        foreach (var prefix in new[] { "../", "audio/../", "/audio/", "audio//", "Audio/", "a/b/c/d/e/", "audio" })
            await Assert.ThrowsAsync<ArgumentException>(() => repo.ReserveAsync(Request() with { KeyPrefix = prefix }, "owner", 1, 100, Token));
        Assert.Equal(0, await db.AudioAssets.CountAsync(Token)); Assert.Equal(0, await db.AudioUploadOperations.CountAsync(Token));
        var custom = await ReserveAsync(repo, Request() with { KeyPrefix = "private/audio/" });
        Assert.Matches("^private/audio/[0-9a-f]{32}\\.mp3$", custom.Asset.Location.Key);
    }

    [Fact]
    public async Task AudioOperationsRejectEnclosingTransactionToKeepTransportOutsideLock()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db); var repo = new AudioUploadRepository(db, new ManualClock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(() =>
            repo.ReserveAsync(Request(), "owner", 1, 100, Token)));
        Assert.Equal(0, await db.AudioAssets.CountAsync(Token));
    }

    [Fact]
    public async Task PublicationOwnsActualSqliteWriterReservationBeyondProcessSemaphore()
    {
        using var file = new AudioTestDatabase(); await using (var seed = file.Open()) await InitAsync(seed);
        var race = new AttemptExternalWrite(file); await using var db = file.Open(race); var repo = new AudioUploadRepository(db, new ManualClock());
        var upload = await ReserveAsync(repo, Request()); Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        race.Enabled = true; Assert.Equal(AudioUploadState.Succeeded, (await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 3, Token))!.State);
        Assert.True(race.BlockedBySqlite);
    }

    [Fact]
    public async Task LeaseExpiryWhileQueued_IsRecheckedAfterWriterAdmission()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var clock = new ManualClock(); var repo = new AudioUploadRepository(db, clock); var upload = await ReserveAsync(repo, Request());
        Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        await using var other = file.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new UnitOfWork(other).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; });
        await entered.Task;
        var queued = repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 3, Token);
        clock.Now = 300_001; release.SetResult(); await writer;
        Assert.Null(await queued); Assert.Equal(0, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Equal(AudioUploadState.Transferring, (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Operation.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalDeleteAndPublishCompeteOnRealSqlite(bool publishFirst)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var first = await ReserveAsync(repo, Request()); await PublishAsync(repo, first);
        var replacing = await ReserveAsync(repo, Request(expected: first.Asset.Id));
        Assert.True(await repo.BeginTransferAsync(replacing.Operation.IdempotencyKey, replacing.Operation.UploadLease, 2, Token));
        await using var deleteDb = file.Open(); await using var holderDb = file.Open();
        var deleting = new AudioUnbindService(new AudioUploadRepository(deleteDb, new ManualClock()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UnitOfWork(holderDb).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Task<AudioUnbindDisposition> delete; Task<AudioUploadOperation?> publish;
        if (publishFirst)
        {
            publish = repo.CompleteAsync(replacing.Operation.IdempotencyKey, replacing.Operation.UploadLease, 3, Token);
            delete = deleting.UnbindAsync("word", null, AudioLocale.EnGb, first.Asset.Id, Token);
        }
        else
        {
            delete = deleting.UnbindAsync("word", null, AudioLocale.EnGb, first.Asset.Id, Token);
            publish = repo.CompleteAsync(replacing.Operation.IdempotencyKey, replacing.Operation.UploadLease, 3, Token);
        }
        release.SetResult(); await holder;
        Assert.Equal(publishFirst ? AudioUnbindDisposition.Conflict : AudioUnbindDisposition.Removed, await delete);
        var outcome = Assert.IsType<AudioUploadOperation>(await publish);
        Assert.Equal(publishFirst ? 200 : 409, outcome.Receipt!.StatusCode);
        Assert.Equal(publishFirst ? replacing.Asset.Id : null, await db.VocabularyAudioBindings.Select(x => x.AssetId).SingleOrDefaultAsync(Token));
        Assert.Equal(2, await db.AudioAssets.CountAsync(Token)); Assert.Equal(2, await db.AudioUploadOperations.CountAsync(Token));
        Assert.Equal(200, (await repo.GetAsync(first.Operation.IdempotencyKey, Token))!.Operation.Receipt!.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConditionalDeleteAndTargetDelete_KeepHistoryWithOneLegalResult(bool meaning, bool targetFirst)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var upload = await ReserveAsync(repo, Request(meaning ? "meaning" : null)); await PublishAsync(repo, upload);
        var history = await repo.GetAsync(upload.Operation.IdempotencyKey, Token);
        await using var other = file.Open(); await using var holderDb = file.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UnitOfWork(holderDb).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Task<int> RemoveTarget() => new UnitOfWork(other).ExecuteInTransactionAsync(async () =>
            meaning ? await other.VocabularyMeanings.Where(x => x.Id == "meaning").ExecuteDeleteAsync(Token)
                : await other.Vocabularies.Where(x => x.Id == "word").ExecuteDeleteAsync(Token));
        Task<AudioUnbindDisposition> delete; Task<int> target;
        if (targetFirst) { target = RemoveTarget(); delete = new AudioUnbindService(repo).UnbindAsync("word", meaning ? "meaning" : null, AudioLocale.EnGb, upload.Asset.Id, Token); }
        else { delete = new AudioUnbindService(repo).UnbindAsync("word", meaning ? "meaning" : null, AudioLocale.EnGb, upload.Asset.Id, Token); target = RemoveTarget(); }
        release.SetResult(); await holder; Assert.Equal(1, await target);
        Assert.Equal(targetFirst ? AudioUnbindDisposition.NotFound : AudioUnbindDisposition.Removed, await delete);
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Token)); Assert.Equal(history, await repo.GetAsync(upload.Operation.IdempotencyKey, Token));
    }

    [Fact]
    public async Task TwoConditionalDeletes_RemoveOnceAndPreserveReceipt()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var upload = await ReserveAsync(repo, Request()); await PublishAsync(repo, upload);
        await using var other = file.Open();
        var a = new AudioUnbindService(repo).UnbindAsync("word", null, AudioLocale.EnGb, upload.Asset.Id, Token);
        var b = new AudioUnbindService(new AudioUploadRepository(other, new ManualClock())).UnbindAsync("word", null, AudioLocale.EnGb, upload.Asset.Id, Token);
        Assert.Equal(new[] { AudioUnbindDisposition.Removed, AudioUnbindDisposition.NotFound }.Order(), (await Task.WhenAll(a, b)).Order());
        Assert.Equal(200, (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Operation.Receipt!.StatusCode);
    }

    [Fact]
    public async Task ConditionalDeleteCancellation_BeforeWhileQueuedAndAfterAdmission()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new AudioUploadRepository(db, new ManualClock()); var upload = await ReserveAsync(repo, Request()); await PublishAsync(repo, upload);
        var service = new AudioUnbindService(repo); using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UnbindAsync("word", null, AudioLocale.EnGb, upload.Asset.Id, before.Token));
        await using var holderDb = file.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UnitOfWork(holderDb).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); using var queuedCancel = new CancellationTokenSource();
        var queued = service.UnbindAsync("word", null, AudioLocale.EnGb, upload.Asset.Id, queuedCancel.Token);
        queuedCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); release.SetResult(); await holder;
        Assert.Single(await db.VocabularyAudioBindings.ToListAsync(Token));
        using var admitted = new CancellationTokenSource(); await using var deleteDb = file.Open(new CancelOnUnbind(admitted));
        Assert.Equal(AudioUnbindDisposition.Removed, await new AudioUnbindService(new AudioUploadRepository(deleteDb, new ManualClock()))
            .UnbindAsync("word", null, AudioLocale.EnGb, upload.Asset.Id, admitted.Token));
        Assert.True(admitted.IsCancellationRequested); Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Token));
        Assert.Equal(200, (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Operation.Receipt!.StatusCode);
        Assert.NotNull((await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Asset.FirstPublishedAtUnixMs);
    }

    private sealed class CancelOnUnbind(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM \"vocabulary_audio_binding\"", StringComparison.Ordinal)) source.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Now { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    private sealed class AttemptExternalWrite(AudioTestDatabase file) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool BlockedBySqlite { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !BlockedBySqlite && command.CommandText.Contains("FROM \"vocabulary\" AS", StringComparison.Ordinal))
            {
                // Bypass UnitOfWork and its process semaphore entirely: SQLite
                // itself must prevent an external writer from changing input.
                await using var connection = new SqliteConnection($"Data Source={file.PathName};Pooling=False;Default Timeout=1");
                await connection.OpenAsync(cancellationToken);
                await using var change = connection.CreateCommand(); change.CommandTimeout = 1;
                change.CommandText = "UPDATE vocabulary SET phonetic_uk='external-change' WHERE id='word'";
                try { await change.ExecuteNonQueryAsync(cancellationToken); }
                catch (SqliteException error) when (error.SqliteErrorCode == 5) { BlockedBySqlite = true; }
            }
            return result;
        }
    }

    private sealed class FailSuccessUpdate : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("UPDATE \"audio_upload_operation\"", StringComparison.Ordinal)
                && command.CommandText.Contains("receipt_asset_id", StringComparison.Ordinal)) throw new InvalidOperationException("Injected success write failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CancelOnInsert(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO \"audio_asset\"", StringComparison.Ordinal)) source.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
