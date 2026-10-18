using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class TextbookAudioRecoveryTests
{
    private const long Day = 86400000;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("reserved")]
    [InlineData("before-put")]
    [InlineData("after-put")]
    [InlineData("abandoned")]
    [InlineData("response-lost")]
    public async Task RestartAtEveryStage_PreservesSuccessAndRecoversKnownUnpublishedObjects(string stage)
    {
        using var file = new AudioTestDatabase(); var clock = new Clock(); var store = new Store(); TextbookAudioStoredUpload upload;
        TextbookAudioUploadReceipt? originalReceipt = null;
        await using (var db = file.Open())
        {
            await Init(db); var repo = new TextbookAudioUploadRepository(db, clock); upload = await Reserve(repo);
            if (stage != "reserved") await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, clock.Now, Token);
            if (stage is "after-put" or "response-lost" or "abandoned") store.Objects.Add(upload.Asset.Location);
            if (stage == "abandoned") await repo.AbandonAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, clock.Now, 503, "audio.upload_cancelled", Token);
            if (stage == "response-lost") await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, clock.Now, Token);
            originalReceipt = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Operation.Receipt;
        }
        clock.Now = Day + 1;
        await using (var db = file.Open())
        {
            var repo = new TextbookAudioUploadRepository(db, clock); await Service(repo, store, clock).RunBatchAsync(Token);
            var result = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!;
            Assert.Equal(stage == "response-lost" ? AudioUploadState.Succeeded : AudioUploadState.CleanupObserved, result.Operation.State);
            Assert.Equal(stage == "response-lost" ? 200 : 503, result.Operation.Receipt!.StatusCode);
            Assert.Equal(stage == "response-lost" ? 1 : 0, await db.TextbookAudioTracks.CountAsync(t => t.CurrentAssetId != null, Token));
            Assert.Equal(stage == "response-lost", store.Objects.Contains(upload.Asset.Location));
            Assert.Equal(upload.Asset.Location, result.Asset.Location);
            if (originalReceipt != null) Assert.Equal(originalReceipt, result.Operation.Receipt);
            Assert.Null(await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, clock.Now, Token));
        }
    }

    [Fact]
    public async Task ValidLeaseAndPublishedUnreferencedAssets_AreNeverDeleted()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db);
        var clock = new Clock(); var repo = new TextbookAudioUploadRepository(db, clock); var store = new Store();
        var published = await Reserve(repo); await repo.BeginTransferAsync(published.Operation.IdempotencyKey, published.Operation.UploadLease, 0, Token);
        await repo.CompleteAsync(published.Operation.IdempotencyKey, published.Operation.UploadLease, 0, Token);
        await db.TextbookAudioTracks.ExecuteDeleteAsync(Token);
        await db.TextbookAudioTracks.AddAsync(new() { Id = "track", BookId = "book", UnitId = "unit", Title = "New", Version = TextbookAudioUploadRepositoryTests.Version }, Token);
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        var active = await Reserve(repo, lease: 2 * Day); store.Objects.UnionWith([published.Asset.Location, active.Asset.Location]);
        clock.Now = Day + 1; Assert.Equal(0, (await Service(repo, store, clock).RunBatchAsync(Token)).Examined);
        Assert.Empty(store.Removed); Assert.Equal(2, store.Objects.Count);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM textbook_audio_track; DELETE FROM vocabulary_meaning_unit; DELETE FROM vocabulary_meaning; DELETE FROM vocabulary; DELETE FROM vocabulary_book_unit; DELETE FROM vocabulary_book", Token);
        await Service(repo, store, clock).RunBatchAsync(Token); Assert.Empty(store.Removed);
    }

    [Fact]
    public async Task LatePutAndRemoveFailures_RetainReceiptAndBackOffAcrossRestarts()
    {
        using var file = new AudioTestDatabase(); var clock = new Clock(); var store = new Store(); TextbookAudioStoredUpload upload;
        await using (var db = file.Open()) { await Init(db); upload = await Reserve(new TextbookAudioUploadRepository(db, clock)); }
        clock.Now = Day + 1;
        await using (var db = file.Open())
        {
            var repo = new TextbookAudioUploadRepository(db, clock); var service = Service(repo, store, clock);
            await service.RunBatchAsync(Token); var first = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!;
            store.Objects.Add(upload.Asset.Location); // Remote timed-out Put finishes after the first Remove.
            Assert.Equal(0, (await service.RunBatchAsync(Token)).Examined);
            clock.Now = first.Operation.Cleanup.NextAttemptAtUnixMs!.Value; store.Fail = true;
            Assert.Equal(1, (await service.RunBatchAsync(Token)).Failed);
            var failed = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!;
            Assert.Equal(first.Operation.Receipt, failed.Operation.Receipt); Assert.Equal(AudioUploadState.CleanupObserved, failed.Operation.State);
            Assert.Equal("audio.cleanup_failed", failed.Operation.Cleanup.LastErrorCode);
            Assert.Equal(clock.Now + 120000, failed.Operation.Cleanup.NextAttemptAtUnixMs);
            Assert.Equal(0, (await service.RunBatchAsync(Token)).Examined); clock.Now = failed.Operation.Cleanup.NextAttemptAtUnixMs!.Value;
        }
        await using (var db = file.Open())
        {
            var repo = new TextbookAudioUploadRepository(db, clock); store.Fail = false;
            Assert.Equal(1, (await Service(repo, store, clock).RunBatchAsync(Token)).Observed);
            Assert.Empty(store.Objects); var retained = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!;
            Assert.Equal(3, retained.Operation.Cleanup.Attempts); Assert.NotNull(retained.Operation.Receipt);
            Assert.Equal(upload.Asset.Location, retained.Asset.Location);
        }
    }

    [Fact]
    public async Task MultipleWorkersAndBlockedStorage_UseSqliteClaimsWithoutHoldingWriterLock()
    {
        using var file = new AudioTestDatabase(); await using var firstDb = file.Open(); await Init(firstDb);
        await using var secondDb = file.Open(); var clock = new Clock(); var first = new TextbookAudioUploadRepository(firstDb, clock);
        var second = new TextbookAudioUploadRepository(secondDb, clock); var upload = await Reserve(first);
        clock.Now = Day + 1; var store = new Store { Block = true };
        var task = Service(first, store, clock).RunBatchAsync(Token); await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(0, (await Service(second, store, clock).RunBatchAsync(Token)).Examined);
        Assert.Null(await second.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, clock.Now, Token));
        await secondDb.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET title='edited' WHERE id='track'", Token);
        store.Release.TrySetResult(); Assert.Equal(1, (await task).Observed); Assert.Single(store.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownOrTimeout_WithUncooperativeStorage_IsBoundedAndRecoverable(bool timeout)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db);
        var clock = new Clock(); var repo = new TextbookAudioUploadRepository(db, clock); var upload = await Reserve(repo);
        clock.Now = Day + 1; var store = new Store { Block = true }; using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var service = new AudioRecoveryService(repo, store, new(true, "test-audio", "audio/", TimeoutSeconds: 1), clock);
        var task = service.RunBatchAsync(stop.Token); await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (!timeout) stop.Cancel();
        if (timeout) Assert.Equal(1, (await task.WaitAsync(TimeSpan.FromSeconds(3), Token)).Failed);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(3), Token));
        var retained = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!;
        Assert.NotNull(retained.Operation.Receipt); Assert.Equal(upload.Asset.Location, retained.Asset.Location);
        store.Release.TrySetResult(); store.Block = false;
        clock.Now += 300001; await service.RunBatchAsync(Token); clock.Now += 3600000;
        await service.RunBatchAsync(Token); Assert.Equal(AudioUploadState.CleanupObserved, (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Operation.State);
    }

    [Fact]
    public async Task DisabledBatchAndControlledLocationGuard_DoNotContactStorage()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db);
        var clock = new Clock(); var repo = new TextbookAudioUploadRepository(db, clock); var upload = await Reserve(repo); clock.Now = Day + 1; var store = new Store();
        var disabled = new AudioRecoveryService(repo, store, new(false, "test-audio", "audio/"), clock);
        Assert.Equal(0, (await disabled.RunBatchAsync(Token)).Examined);
        await db.AudioAssets.Where(row => row.Id == upload.Asset.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Bucket, "images"), Token);
        Assert.Equal(1, (await Service(repo, store, clock).RunBatchAsync(Token)).Failed); Assert.Empty(store.Removed);
    }

    [Fact]
    public async Task BatchLimit_AndExpiredCleanupFence_ArePersisted()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db);
        var clock = new Clock(); var repo = new TextbookAudioUploadRepository(db, clock); var uploads = new[] { await Reserve(repo), await Reserve(repo) };
        clock.Now = Day + 1; var store = new Store();
        var service = new AudioRecoveryService(repo, store, new(true, "test-audio", "audio/", BatchSize: 1), clock);
        Assert.Equal(1, (await service.RunBatchAsync(Token)).Examined); Assert.Equal(1, (await service.RunBatchAsync(Token)).Examined);
        Assert.Equal(2, store.Removed.Count);
        var op = (await repo.GetAsync(uploads[0].Operation.IdempotencyKey, Token))!; clock.Now = op.Operation.Cleanup.NextAttemptAtUnixMs!.Value;
        var claim = (await repo.ClaimCleanupAsync(op.Operation.IdempotencyKey, "dead-worker", clock.Now, clock.Now + 100, Token))!;
        clock.Now += 100; await Service(repo, store, clock).RunBatchAsync(Token);
        Assert.False(await repo.ObserveCleanupAsync(op.Operation.IdempotencyKey, claim.Operation.Cleanup.Lease, clock.Now, clock.Now + 1000, Token));
        Assert.Equal(AudioUploadState.CleanupObserved, (await repo.GetAsync(op.Operation.IdempotencyKey, Token))!.Operation.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StatFailureOrObjectStillPresent_RetriesPersistentlyWithCappedDelay(bool statFailure)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db);
        var clock = new Clock(); var repo = new TextbookAudioUploadRepository(db, clock); var upload = await Reserve(repo);
        clock.Now = Day + 1; var store = new Store { StatFail = statFailure, KeepObject = !statFailure };
        store.Objects.Add(upload.Asset.Location);
        var service = new AudioRecoveryService(repo, store, new(true, "test-audio", "audio/", RetrySeconds: 60, MaxRetrySeconds: 120), clock);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal(1, (await service.RunBatchAsync(Token)).Failed);
            var operation = (await repo.GetAsync(upload.Operation.IdempotencyKey, Token))!.Operation;
            Assert.Equal(AudioUploadState.Abandoned, operation.State); Assert.Equal(attempt, operation.Cleanup.Attempts);
            Assert.Equal(clock.Now + (attempt == 1 ? 60000 : 120000), operation.Cleanup.NextAttemptAtUnixMs);
            Assert.Equal(0, (await service.RunBatchAsync(Token)).Examined);
            clock.Now = operation.Cleanup.NextAttemptAtUnixMs!.Value;
        }
        store.StatFail = false; store.KeepObject = false;
        Assert.Equal(1, (await service.RunBatchAsync(Token)).Observed);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task MixedQueues_ShareOneBudgetAndStableOrderAcrossRestarts(int batchSize, bool textbookFirst)
    {
        using var file = new AudioTestDatabase(); var clock = new Clock(); var store = new Store();
        var types = new Dictionary<AudioObjectLocation, bool>();
        await using (var db = file.Open())
        {
            await Init(db);
            var vocabulary = new AudioUploadRepository(db, clock); var textbook = new TextbookAudioUploadRepository(db, clock);
            for (var i = 0; i < 5; i++)
            {
                var word = (await vocabulary.ReserveAsync(new(Guid.NewGuid().ToString(), AudioTestDatabase.Hash, "word", null,
                    AudioLocale.EnGb, AudioTestDatabase.Revision(AudioLocale.EnGb), null, AudioTestDatabase.Hash, 128, "test-audio"), "owner", 0, 300000, Token)).Upload!;
                types.Add(word.Asset.Location, false); types.Add((await Reserve(textbook)).Asset.Location, true);
            }
        }
        clock.Now = Day + 1;
        while (store.Removed.Count < types.Count)
        {
            await using var db = file.Open(); // Simulate restart with a fresh context/runner.
            var vocabulary = new AudioUploadRepository(db, clock); var textbook = new TextbookAudioUploadRepository(db, clock);
            var words = await vocabulary.FindRecoverableAsync(clock.Now, batchSize, Token);
            var texts = await textbook.FindRecoverableAsync(clock.Now, batchSize, Token);
            var before = store.Removed.Count;
            var result = await new AudioRecoveryService(vocabulary, textbook, store,
                new(true, "test-audio", "audio/", BatchSize: batchSize), clock).RunBatchAsync(Token, textbookFirst);
            Assert.InRange(result.Examined, 1, batchSize); Assert.Equal(result.Examined, result.Observed);
            var batch = store.Removed.Skip(before).ToArray();
            Assert.Equal(words.Take(batch.Count(x => !types[x])).Select(x => x.Asset.Location), batch.Where(x => !types[x]));
            Assert.Equal(texts.Take(batch.Count(x => types[x])).Select(x => x.Asset.Location), batch.Where(x => types[x]));
            if (words.Count > 0 && texts.Count > 0) Assert.Equal(textbookFirst, types[batch[0]]);
            textbookFirst = !textbookFirst;
        }
        Assert.Equal(10, store.Removed.Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOtherQueue_FillsEntireSharedBudget(bool onlyTextbook)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db);
        var clock = new Clock(); var word = new AudioUploadRepository(db, clock); var text = new TextbookAudioUploadRepository(db, clock);
        for (var i = 0; i < 4; i++)
            if (onlyTextbook) await Reserve(text);
            else await word.ReserveAsync(new(Guid.NewGuid().ToString(), AudioTestDatabase.Hash, "word", null, AudioLocale.EnGb,
                AudioTestDatabase.Revision(AudioLocale.EnGb), null, AudioTestDatabase.Hash, 128, "test-audio"), "owner", 0, 300000, Token);
        clock.Now = Day + 1; var store = new Store();
        var result = await new AudioRecoveryService(word, text, store, new(true, "test-audio", "audio/", BatchSize: 3), clock).RunBatchAsync(Token, !onlyTextbook);
        Assert.Equal(3, result.Examined); Assert.Equal(3, result.Observed);
    }

    private static AudioRecoveryService Service(ITextbookAudioUploadRepository repo, Store store, Clock clock) => new(repo, store, new(true, "test-audio", "audio/"), clock);
    private static Task Init(Lexarbor.Database.VocabularyDbContext db) => TextbookAudioUploadRepositoryTests.InitAsync(db);
    private static async Task<TextbookAudioStoredUpload> Reserve(ITextbookAudioUploadRepository repo, long lease = 300000)
        => (await repo.ReserveAsync(TextbookAudioUploadRepositoryTests.Request(), "uploader", 0, lease, Token)).Upload!;
    private sealed class Clock : TimeProvider { public long Now; public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now); }
    private sealed class Store : IAudioObjectStore
    {
        public readonly HashSet<AudioObjectLocation> Objects = []; public readonly List<AudioObjectLocation> Removed = [];
        public bool Block; public bool Fail; public bool StatFail; public bool KeepObject;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RemoveAsync(AudioObjectLocation location, CancellationToken token)
        {
            Entered.TrySetResult(); if (Block) await Release.Task;
            if (Fail) throw new InvalidOperationException("synthetic-provider-detail-must-not-escape");
            Removed.Add(location); if (!KeepObject) Objects.Remove(location);
        }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => StatFail ? throw new InvalidOperationException("synthetic-stat-provider-detail") : Task.FromResult(Objects.Contains(location) ? new AudioObjectInfo(128, "audio/mpeg") : null);
        public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, CancellationToken token) => throw new NotSupportedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => throw new NotSupportedException();
    }
}
