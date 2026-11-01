using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class EnrichmentTaskRepositoryTests
{
    private static CancellationToken Token => AudioTestDatabase.Token;
    private static EnrichmentTargetKey Key(AudioLocale locale = AudioLocale.EnGb, string? meaning = null,
        EnrichmentStep step = EnrichmentStep.Audio) => new("word", meaning, locale, step);
    private static async Task InitAsync(VocabularyDbContext db)
    { await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db); }
    private static async Task<EnrichmentTask> EnsureAsync(EnrichmentTaskRepository repo, EnrichmentTargetKey? key = null,
        string config = "config-1", long now = 1) => Assert.IsType<EnrichmentTask>(await repo.EnsureAsync(key ?? Key(), config, now, Token));
    private static async Task<EnrichmentExecutionGuard> ClaimAsync(EnrichmentTaskRepository repo, string id, long now = 2, long expires = 1000) =>
        Assert.IsType<EnrichmentExecutionGuard>(await repo.ClaimAsync(id, "worker", now, expires, Token));

    [Fact]
    public async Task TwoConnections_DeduplicateTargetsAndAllowExactlyOneCurrentOwner()
    {
        using var file = new AudioTestDatabase(); await using var a = file.Open(); await InitAsync(a);
        await using var b = file.Open(); var clock = new Clock();
        var first = new EnrichmentTaskRepository(a, clock); var second = new EnrichmentTaskRepository(b, clock);
        var ensured = await Task.WhenAll(first.EnsureAsync(Key(), "config-1", 1, Token), second.EnsureAsync(Key(), "config-1", 1, Token));
        Assert.Equal(ensured[0]!.Id, ensured[1]!.Id); Assert.Equal(1, await a.EnrichmentTargets.CountAsync(Token));
        var claims = await Task.WhenAll(first.ClaimAsync(ensured[0]!.Id, "first", 2, 100, Token), second.ClaimAsync(ensured[0]!.Id, "second", 2, 100, Token));
        var guard = Assert.Single(claims.OfType<EnrichmentExecutionGuard>());
        foreach (var wrong in new[] { guard with { Owner = "wrong" }, guard with { Fence = guard.Fence + 1 },
            guard with { Round = 2 }, guard with { Epoch = guard.Epoch + 1 }, guard with { Input = guard.Input with { OwnAssetId = "wrong" } } })
        {
            Assert.False(await first.SavePlanAsync(wrong, new("apple", null, null, null), 3, Token));
            Assert.False(await first.CompleteAsync(wrong, 3, Token));
            Assert.False(await new UnitOfWork(a).ExecuteInTransactionAsync(() => first.ValidateInTransactionAsync(wrong, 3, Token), Token));
        }
        Assert.True(await first.RenewAsync(guard, 3, 200, Token));
        Assert.False(await first.RenewAsync(guard, 3, 150, Token));
        Assert.Null(await second.ClaimAsync(guard.TargetId, "other", 199, 300, Token));
        Assert.False(await first.CompleteAsync(guard, 200, Token));
        var reclaimed = Assert.IsType<EnrichmentExecutionGuard>(await second.ClaimAsync(guard.TargetId, "other", 200, 300, Token));
        Assert.Equal(guard.Fence + 1, reclaimed.Fence); Assert.Equal(guard.Round, reclaimed.Round);
        Assert.False(await first.CompleteAsync(guard, 201, Token)); Assert.True(await second.CompleteAsync(reclaimed, 201, Token));
    }

    [Fact]
    public async Task QueuedWriter_RechecksClockAfterAdmissionAndCannotUseAStalePrecheck()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var clock = new Clock(); var repo = new EnrichmentTaskRepository(db, clock);
        var task = await EnsureAsync(repo); var guard = await ClaimAsync(repo, task.Id, 2, 100);
        await using var holderDb = file.Open(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UnitOfWork(holderDb).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var completing = repo.CompleteAsync(guard, 3, Token); clock.Now = 100; release.SetResult(); await holder;
        Assert.False(await completing); Assert.Null(await repo.ClaimAsync(task.Id, "late", 3, 99, Token));
        Assert.False(await repo.RenewAsync(guard, 3, 200, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ValidateInTransactionAsync(guard, 3, Token));
        var fresh = await ClaimAsync(repo, task.Id, 100, 300);
        Assert.True(await new UnitOfWork(db).ExecuteInTransactionAsync(() => repo.ValidateInTransactionAsync(fresh, 101, Token), Token));
        await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET phonetic_uk='changed' WHERE id='word'", Token);
            Assert.Null(await repo.AdvanceInputInTransactionAsync(fresh, 102, Token)); // authorization belonged to a different transaction
            return 0;
        }, Token);
        Assert.False(await repo.CompleteAsync(fresh, 103, Token));
    }

    [Fact]
    public async Task GuardedFieldAndBindingWrites_ShareTheRealTransactionAndAdvanceOnlyTheSameRound()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new EnrichmentTaskRepository(db, new Clock()); var task = await EnsureAsync(repo); var guard = await ClaimAsync(repo, task.Id);
        var next = await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            Assert.True(await repo.ValidateInTransactionAsync(guard, 3, Token));
            // A synthetic final writer exercises the contract used by #342;
            // no production enrichment executor is activated in this foundation.
            await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET phonetic_uk='filled' WHERE id='word'", Token);
            db.AudioAssets.Add(AudioTestDatabase.Asset("generated"));
            var changedInput = Assert.IsType<EnrichmentInputVersion>(await repo.ReadInputAsync(Key(), Token));
            db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("generated", AudioLocale.EnGb, null, changedInput.SourceRevision));
            await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
            return Assert.IsType<EnrichmentExecutionGuard>(await repo.AdvanceInputInTransactionAsync(guard, 3, Token));
        }, Token);
        Assert.Equal(guard.Round, next.Round); Assert.Equal(guard.Epoch, next.Epoch); Assert.NotEqual(guard.Input, next.Input);
        Assert.Equal("generated", next.Input.OwnAssetId); Assert.False(await repo.CompleteAsync(guard, 4, Token));
        Assert.True(await repo.CompleteAsync(next, 4, Token));
        var scanned = await EnsureAsync(repo, now: 5); Assert.Single(scanned.Rounds); Assert.Equal(EnrichmentState.Succeeded, scanned.Rounds[0].State);
        Assert.Equal("filled", await db.Vocabularies.Where(row => row.Id == "word").Select(row => row.PhoneticUk).SingleAsync(Token));
    }

    [Fact]
    public async Task Invalidation_RollsBackAtomicallyAndAnAbaChangeNeverRevivesAnOldGuard()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='A' WHERE id='meaning'", Token);
        var repo = new EnrichmentTaskRepository(db, new Clock()); var key = Key(meaning: "meaning"); var task = await EnsureAsync(repo, key);
        var original = await ClaimAsync(repo, task.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(db).ExecuteInTransactionAsync<int>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='B' WHERE id='meaning'", Token);
            await repo.InvalidateInTransactionAsync(key, "config-1", 3, Token);
            throw new InvalidOperationException("Injected transaction failure.");
        }, Token));
        Assert.True(await new UnitOfWork(db).ExecuteInTransactionAsync(() => repo.ValidateInTransactionAsync(original, 4, Token), Token));
        foreach (var value in new[] { "B", "A" })
            await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
            {
                await db.VocabularyMeanings.Where(row => row.Id == "meaning").ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, value), Token);
                return await repo.InvalidateInTransactionAsync(key, "config-1", 5, Token);
            }, Token);
        var current = await ClaimAsync(repo, task.Id, 6);
        Assert.Equal(original.Input, current.Input); Assert.True(current.Epoch > original.Epoch); Assert.Equal(3, current.Round);
        var writes = 0;
        await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            if (await repo.ValidateInTransactionAsync(original, 7, Token))
                writes = await db.Vocabularies.Where(row => row.Id == "word").ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "stale-result"), Token);
            return 0;
        }, Token);
        Assert.Equal(0, writes); Assert.False(await repo.CompleteAsync(original, 7, Token));
        Assert.True(await repo.CompleteAsync(current, 7, Token));
        Assert.Equal(3, (await repo.GetAsync(task.Id, Token))!.Rounds.Count);
    }

    [Fact]
    public async Task ThrottlesAndWaitsNeverConsumeFailures_TheFifthFailureStaysExhaustedAcrossRestart()
    {
        using var file = new AudioTestDatabase(); string id; long now = 2;
        await using (var db = file.Open())
        {
            await InitAsync(db); var repo = new EnrichmentTaskRepository(db, new Clock()); id = (await EnsureAsync(repo)).Id;
            foreach (var reason in new[] { EnrichmentWaitReason.Disabled, EnrichmentWaitReason.ConfigurationMissing, EnrichmentWaitReason.StorageDisabled, EnrichmentWaitReason.ServicePaused })
            {
                var guard = await ClaimAsync(repo, id, now); Assert.True(await repo.WaitAsync(guard, reason, now, now + 10, Token));
                Assert.Null(await repo.ClaimAsync(id, "early", now + 9, 1000, Token));
                var waiting = (await repo.GetAsync(id, Token))!.Rounds[0]; Assert.Equal(reason, waiting.WaitReason);
                Assert.Equal(0, waiting.RequestCount); Assert.Equal(0, waiting.ConsumedFailures); now += 10;
            }
            for (var i = 0; i < 7; i++)
            {
                var guard = await ClaimAsync(repo, id, now); var call = Guid.NewGuid().ToString();
                Assert.True(await repo.BeginCallAsync(guard, call, "tts", Guid.NewGuid().ToString(), now, Token));
                Assert.False(await repo.BeginCallAsync(guard, call, "tts", null, now, Token));
                Assert.True(await repo.FinishCallAsync(guard, call, EnrichmentCallResult.Throttled, i % 2 == 0 ? "tts.http_429" : "tts.business_throttle", now, now + 10, Token));
                Assert.False(await repo.FinishCallAsync(guard, call, EnrichmentCallResult.TransientFailure, "tts.failed", now, now + 10, Token)); now += 10;
            }
            var throttled = (await repo.GetAsync(id, Token))!; Assert.Equal(7, throttled.Rounds[0].RequestCount); Assert.Equal(0, throttled.Rounds[0].ConsumedFailures);
            Assert.Equal(7, throttled.Calls.Count); Assert.All(throttled.Calls, call => Assert.NotNull(call.ErrorCode));
            for (var i = 0; i < 5; i++)
            {
                var guard = await ClaimAsync(repo, id, now); var call = Guid.NewGuid().ToString();
                Assert.True(await repo.BeginCallAsync(guard, call, "tts", null, now, Token));
                Assert.True(await repo.FinishCallAsync(guard, call, EnrichmentCallResult.TransientFailure, "tts.timeout", now, now + 10, Token)); now += 10;
            }
        }
        await using var restart = file.Open(); var resumed = new EnrichmentTaskRepository(restart, new Clock());
        var exhausted = await EnsureAsync(resumed, now: now); Assert.Equal(id, exhausted.Id); Assert.Single(exhausted.Rounds);
        Assert.Equal(EnrichmentState.Failed, exhausted.Rounds[0].State); Assert.Equal(5, exhausted.Rounds[0].ConsumedFailures);
        Assert.Equal(12, exhausted.Rounds[0].RequestCount); Assert.Equal(15, exhausted.Waits.Count);
        Assert.Equal(7, exhausted.Waits.Count(wait => wait.Reason == EnrichmentWaitReason.Throttled));
        Assert.Null(await resumed.ClaimAsync(id, "service-recovered", now + 10000, now + 20000, Token));
        Assert.Equal(1, (await resumed.EnsureAsync(Key(), "config-1", now, Token, failureBudget: 20))!.CurrentRound);
        var corrected = await EnsureAsync(resumed, config: "config-2", now: now); Assert.Equal(2, corrected.CurrentRound);
        Assert.Equal(12, corrected.Calls.Count); Assert.Equal(0, corrected.Rounds[1].ConsumedFailures);
        var retryKey = Guid.NewGuid().ToString();
        Assert.Equal(3, (await resumed.EnsureAsync(Key(), "config-2", now, Token, retryKey: retryKey))!.CurrentRound);
        var otherKey = Guid.NewGuid().ToString();
        Assert.Equal(4, (await resumed.EnsureAsync(Key(), "config-2", now, Token, retryKey: otherKey))!.CurrentRound);
        Assert.Equal(4, (await resumed.EnsureAsync(Key(), "config-2", now, Token, retryKey: retryKey))!.CurrentRound);
    }

    [Fact]
    public async Task PartialSuccess_PlanAndUnknownUploadKeySurviveRestartAndPermanentReceiptRecovery()
    {
        using var file = new AudioTestDatabase(); string ukId; string usId; string uploadKey; string callId;
        await using (var db = file.Open())
        {
            await InitAsync(db); var repo = new EnrichmentTaskRepository(db, new Clock());
            ukId = (await EnsureAsync(repo)).Id; usId = (await EnsureAsync(repo, Key(AudioLocale.EnUs))).Id;
            var uk = await ClaimAsync(repo, ukId); Assert.True(await repo.SavePlanAsync(uk, new("apple", "UK", "uk", null), 3, Token));
            Assert.True(await repo.CompleteAsync(uk, 4, Token));
            var us = await ClaimAsync(repo, usId); uploadKey = Guid.NewGuid().ToString(); callId = Guid.NewGuid().ToString();
            Assert.True(await repo.SavePlanAsync(us, new("apple", "US", "us", [new("meaning", "n.")], "word"), 3, Token));
            Assert.True(await repo.BeginCallAsync(us, callId, "tts", uploadKey, 3, Token));
            Assert.False(await repo.CompleteAsync(us, 4, Token));
            var uploads = new AudioUploadRepository(db, new Clock());
            var request = new AudioUploadRequest(uploadKey, AudioTestDatabase.Hash, "word", null, AudioLocale.EnUs,
                us.Input.SourceRevision, null, AudioTestDatabase.Hash, 128, "test-audio");
            var upload = Assert.IsType<AudioStoredUpload>((await uploads.ReserveAsync(request, "uploader", 4, 100, Token)).Upload);
            Assert.True(await uploads.BeginTransferAsync(uploadKey, upload.Operation.UploadLease, 5, Token));
            Assert.Equal(200, (await uploads.CompleteAsync(uploadKey, upload.Operation.UploadLease, 6, Token))!.Receipt!.StatusCode);
            // Crash: upload committed; no task completion or provider outcome was recorded.
        }
        await using var restart = file.Open(); var resumed = new EnrichmentTaskRepository(restart, new Clock());
        var before = (await resumed.GetAsync(usId, Token))!; Assert.Equal("apple", before.Rounds[0].Plan!.SpokenText);
        Assert.Equal("word", before.Rounds[0].Plan!.InferredEntryKind);
        Assert.Equal("meaning", Assert.Single(before.Rounds[0].Plan!.PartsOfSpeech!).MeaningId);
        var unknown = Assert.Single(before.Calls); Assert.Equal(callId, unknown.Id); Assert.Equal(uploadKey, unknown.UploadIdempotencyKey);
        Assert.Equal(EnrichmentCallResult.Started, unknown.Result);
        Assert.False(await resumed.RecoverPublishedAsync(usId, Guid.NewGuid().ToString(), 2000, Token));
        Assert.True(await resumed.RecoverPublishedAsync(usId, uploadKey, 2000, Token));
        Assert.True(await resumed.RecoverPublishedAsync(usId, uploadKey, 2001, Token));
        var recovered = (await resumed.GetAsync(usId, Token))!;
        Assert.Equal(EnrichmentState.Succeeded, recovered.Rounds[0].State); Assert.Equal(1, recovered.Rounds[0].RequestCount);
        Assert.Equal(0, recovered.Rounds[0].ConsumedFailures); Assert.Single(recovered.Calls);
        Assert.Equal(EnrichmentCallResult.Succeeded, recovered.Calls[0].Result);
        Assert.Equal(EnrichmentState.Succeeded, (await resumed.GetAsync(ukId, Token))!.Rounds[0].State);
        Assert.Equal(1, (await resumed.EnsureAsync(Key(AudioLocale.EnUs), "us-config-2", 2002, Token, retryKey: Guid.NewGuid().ToString()))!.CurrentRound);
        await restart.VocabularyAudioBindings.Where(row => row.WordId == "word" && row.Locale == AudioLocale.EnUs).ExecuteDeleteAsync(Token);
        var nextUs = await EnsureAsync(resumed, Key(AudioLocale.EnUs), "us-config-2", 2002);
        Assert.Equal(2, nextUs.CurrentRound); Assert.False(await resumed.RecoverPublishedAsync(usId, uploadKey, 2003, Token));
        Assert.Equal(EnrichmentState.Succeeded, nextUs.Rounds[0].State);
        Assert.Equal(1, (await resumed.GetAsync(ukId, Token))!.CurrentRound);
        Assert.Single(await restart.AudioUploadOperations.ToListAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetDeletion_CancelsActiveWorkButRetainsAllHistoryAndCannotResurrect(bool meaning)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='specific' WHERE id='meaning'", Token);
        var repo = new EnrichmentTaskRepository(db, new Clock()); var key = Key(meaning: meaning ? "meaning" : null);
        var task = await EnsureAsync(repo, key); var guard = await ClaimAsync(repo, task.Id); var call = Guid.NewGuid().ToString();
        Assert.True(await repo.BeginCallAsync(guard, call, "tts", Guid.NewGuid().ToString(), 3, Token));
        if (meaning) await db.VocabularyMeanings.Where(row => row.Id == "meaning").ExecuteDeleteAsync(Token);
        else await db.Vocabularies.Where(row => row.Id == "word").ExecuteDeleteAsync(Token);
        var deleted = (await repo.GetAsync(task.Id, Token))!; Assert.True(deleted.Deleted); Assert.True(deleted.Epoch > guard.Epoch);
        Assert.Equal(EnrichmentState.Cancelled, deleted.Rounds[0].State); Assert.Single(deleted.Calls);
        Assert.False(await repo.CompleteAsync(guard, 4, Token)); Assert.Null(await repo.ClaimAsync(task.Id, "worker", 4, 100, Token));
        Assert.Equal(task.Id, (await EnsureAsync(repo, key, "corrected", 5)).Id);
        Assert.False(await new UnitOfWork(db).ExecuteInTransactionAsync(() => repo.ValidateInTransactionAsync(guard, 5, Token), Token));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task OnlyExplicitMeaningTargetsAreCreated_AndCurrentInputAndOwnBindingAreRechecked()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new EnrichmentTaskRepository(db, new Clock());
        Assert.Null(await repo.EnsureAsync(Key(meaning: "meaning"), "config", 1, Token));
        Assert.Null(await repo.EnsureAsync(Key(meaning: "other-meaning"), "config", 1, Token));
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='specific' WHERE id='meaning'", Token);
        Assert.Null(await repo.EnsureAsync(Key(meaning: "meaning", step: EnrichmentStep.Fields), "config", 1, Token));
        var explicitTask = await EnsureAsync(repo, Key(meaning: "meaning")); var guard = await ClaimAsync(repo, explicitTask.Id);
        db.AudioAssets.Add(AudioTestDatabase.Asset("other-writer"));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("other-writer", AudioLocale.EnGb, "meaning", guard.Input.SourceRevision));
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        Assert.False(await repo.SavePlanAsync(guard, new("apple", null, null, null), 3, Token));
        Assert.False(await repo.CompleteAsync(guard, 3, Token));
        Assert.False(await new UnitOfWork(db).ExecuteInTransactionAsync(() => repo.ValidateInTransactionAsync(guard, 3, Token), Token));
        await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk=NULL WHERE id='meaning'", Token);
            return await repo.InvalidateInTransactionAsync(Key(meaning: "meaning"), "config", 4, Token);
        }, Token);
        Assert.Equal(EnrichmentState.Cancelled, (await repo.GetAsync(explicitTask.Id, Token))!.Rounds[0].State);
        Assert.Equal(1, await db.EnrichmentTargets.CountAsync(Token));
    }

    [Fact]
    public async Task PlanningInput_UsesExplicitPositionAndPosAndCannotBeAuthorizedFromAnotherContext()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new EnrichmentTaskRepository(db, new Clock()); var task = await EnsureAsync(repo, Key(step: EnrichmentStep.Prepare));
        var guard = await ClaimAsync(repo, task.Id);
        await db.VocabularyMeaningUnits.ExecuteUpdateAsync(set => set.SetProperty(row => row.EntryKind, "phrase"), Token);
        Assert.False(await repo.CompleteAsync(guard, 3, Token));
        var changed = await EnsureAsync(repo, Key(step: EnrichmentStep.Prepare), now: 4); Assert.Equal(2, changed.CurrentRound);
        var current = await ClaimAsync(repo, task.Id, 5);
        await using var differentContext = file.Open(); var wrongRepo = new EnrichmentTaskRepository(differentContext, new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(() => wrongRepo.ValidateInTransactionAsync(current, 6, Token), Token));
        await differentContext.Database.OpenConnectionAsync(Token);
        await using (var foreignTransaction = ((SqliteConnection)differentContext.Database.GetDbConnection()).BeginTransaction(deferred: true))
        await using (await differentContext.Database.UseTransactionAsync(foreignTransaction, Token))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(() => wrongRepo.ValidateInTransactionAsync(current, 6, Token), Token));
        await differentContext.Database.CloseConnectionAsync();
        await db.Database.BeginTransactionAsync(Token);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ValidateInTransactionAsync(current, 6, Token)); }
        finally { await db.Database.RollbackTransactionAsync(Token); }
        Assert.True(await repo.StopAsync(current, "enrichment.unsupported_pronunciation", 7, Token));
        Assert.Null(await repo.ClaimAsync(task.Id, "service-recovered", 100, 200, Token));
        Assert.Equal(0, (await repo.GetAsync(task.Id, Token))!.Rounds[1].RequestCount);
    }

    [Fact]
    public async Task CancellationBeforeAndWhileQueuedLeavesNoRows_AfterAdmissionFinishesShortCommit()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await InitAsync(db);
        var repo = new EnrichmentTaskRepository(db, new Clock()); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.EnsureAsync(Key(), "config", 1, cancelled.Token));
        await using var holderDb = file.Open(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UnitOfWork(holderDb).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); using var queuedCancel = new CancellationTokenSource();
        var queued = repo.EnsureAsync(Key(), "config", 1, queuedCancel.Token); queuedCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); release.SetResult(); await holder;
        Assert.Empty(await db.EnrichmentTargets.ToListAsync(Token));
        using var admitted = new CancellationTokenSource(); await using var other = file.Open(new CancelOnInsert(admitted));
        Assert.NotNull(await new EnrichmentTaskRepository(other, new Clock()).EnsureAsync(Key(), "config", 1, admitted.Token));
        Assert.True(admitted.IsCancellationRequested); Assert.Single(await db.EnrichmentTargets.ToListAsync(Token));
    }

    private sealed class Clock : TimeProvider
    {
        public long Now { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    private sealed class CancelOnInsert(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO \"enrichment_target\"", StringComparison.Ordinal)) source.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
