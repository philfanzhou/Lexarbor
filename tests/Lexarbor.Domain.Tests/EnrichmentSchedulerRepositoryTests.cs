using System.Data.Common;
using Lexarbor.Database;
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

public sealed class EnrichmentSchedulerRepositoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static EnrichmentServicePolicy Policy => new(5, 60, 600, 8, 120);

    [Fact]
    public async Task ThrottleAndServiceLease_SurviveRestart_RespectRetryAfter_AndRecoverGradually()
    {
        using var file = new AudioTestDatabase();
        await using (var db = file.Open()) await db.Database.MigrateAsync(Ct);
        long now = 1000;
        foreach (var delay in new[] { 8, 16, 32, 64, 120, 120, 120 })
        {
            await using var db = file.Open(); var repo = new EnrichmentSchedulerRepository(db);
            var admitted = await repo.EnterServiceAsync("tts", "version", false, now, now + 10_000, Ct);
            Assert.NotNull(admitted.LeaseId);
            var duplicate = await repo.EnterServiceAsync("tts", "version", false, now, now + 10_000, Ct);
            Assert.Null(duplicate.LeaseId);
            var next = await repo.FinishServiceAsync(admitted, "Throttled", "rate_limited", true, now,
                delay == 64 ? now + 200_000 : null, Policy, Ct);
            Assert.Equal(now + (delay == 64 ? 200 : delay) * 1000L, next);
            Assert.Equal(delay, (await repo.ServiceAsync("tts", Ct))!.ThrottleSeconds);
            Assert.False((await repo.ServiceAsync("tts", Ct))!.Paused);
            now = next;
        }
        await using var restarted = file.Open(); var resumed = new EnrichmentSchedulerRepository(restarted);
        var success = await resumed.EnterServiceAsync("tts", "version", false, now, now + 10_000, Ct);
        var reduced = await resumed.FinishServiceAsync(success, "Ready", null, true, now, null, Policy, Ct);
        Assert.Equal(now + 60_000, reduced);
        Assert.Equal(60, (await resumed.ServiceAsync("tts", Ct))!.ThrottleSeconds);
        Assert.Empty(await resumed.ProbesAsync("tts", Ct));
    }

    [Fact]
    public async Task PauseHasOneIndependentProbe_ExponentialCeiling_AndExpiredProbeRecovery()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Ct);
        var repo = new EnrichmentSchedulerRepository(db); long now = 1000;
        for (var i = 0; i < 5; i++)
        {
            var ticket = await repo.EnterServiceAsync("text", "v1", false, now, now + 5000, Ct);
            await repo.FinishServiceAsync(ticket, "ServiceFailure", "connection_failed", true, now, null, Policy, Ct);
            now++;
        }
        var paused = (await repo.ServiceAsync("text", Ct))!;
        Assert.True(paused.Paused); Assert.Equal(60, paused.PauseSeconds);
        Assert.Equal(EnrichmentWaitReason.ServicePaused, (await repo.EnterServiceAsync("text", "v1", false, now, now + 5000, Ct)).WaitReason);
        Assert.NotNull((await repo.EnterServiceAsync("independent", "v1", false, now, now + 5000, Ct)).LeaseId);
        now = paused.NextAttemptAtUnixMs;
        foreach (var seconds in new[] { 120, 240, 480, 600, 600 })
        {
            var probe = await repo.EnterServiceAsync("text", "v1", true, now, now + 5000, Ct);
            Assert.NotNull(probe.LeaseId);
            await using (var other = file.Open())
                Assert.Null((await new EnrichmentSchedulerRepository(other).EnterServiceAsync("text", "v1", true, now, now + 5000, Ct)).LeaseId);
            now = await repo.FinishServiceAsync(probe, "ServiceFailure", "service_timeout", true, now, null, Policy, Ct);
            Assert.Equal(seconds, (await repo.ServiceAsync("text", Ct))!.PauseSeconds);
        }
        var crashed = await repo.EnterServiceAsync("text", "v1", true, now, now + 5000, Ct);
        await using var restart = file.Open(); var resumed = new EnrichmentSchedulerRepository(restart);
        Assert.Null((await resumed.EnterServiceAsync("text", "v1", true, now, now + 5000, Ct)).LeaseId);
        now += 5001;
        var replacement = await resumed.EnterServiceAsync("text", "v1", true, now, now + 5000, Ct);
        Assert.NotNull(replacement.LeaseId); Assert.NotEqual(crashed.LeaseId, replacement.LeaseId);
        await resumed.FinishServiceAsync(replacement, "Ready", null, true, now, null, Policy, Ct);
        Assert.False((await resumed.ServiceAsync("text", Ct))!.Paused);
        var probes = await resumed.ProbesAsync("text", Ct);
        Assert.Equal(7, probes.Count); Assert.Contains(probes, p => p.Id == crashed.LeaseId && p.Result == "Unknown");
        Assert.True(probes[^1].RequestMade); Assert.Equal("Ready", probes[^1].Result);
        Assert.Empty(await restart.EnrichmentRounds.ToListAsync(Ct));
    }

    [Fact]
    public async Task LostNotificationsAndContinuousArrivals_StillAdvanceBothHistoricalCursors_AndOwnTargets()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Ct);
        await AudioTestDatabase.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='uk' WHERE id='meaning'", Ct);
        db.AudioAssets.Add(AudioTestDatabase.Asset("default"));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("default", AudioLocale.EnGb, null, AudioTestDatabase.Revision(AudioLocale.EnGb)));
        for (var i = 0; i < 8; i++) db.Vocabularies.Add(new() { Id = $"a{i}", Word = $"old{i}" });
        await db.SaveChangesAsync(Ct); db.ChangeTracker.Clear();
        var found = new HashSet<EnrichmentTargetKey>();
        for (var sweep = 0; sweep < 13; sweep++)
        {
            db.Vocabularies.Add(new() { Id = $"z{sweep:D3}", Word = $"new{sweep}" });
            await db.SaveChangesAsync(Ct); db.ChangeTracker.Clear();
            // Reopen on every sweep; intentionally deliver no notifications.
            await using var restarted = file.Open();
            var keys = await new EnrichmentSchedulerRepository(restarted).DiscoverAsync(1, [], Ct);
            Assert.Contains(keys, k => k.WordId == $"z{sweep:D3}");
            Assert.True(keys.Count <= 14); found.UnionWith(keys);
            if (sweep == 12) Assert.Contains(keys, k => k.WordId == "a0"); // A full historical wrap despite new arrivals.
        }
        for (var i = 0; i < 8; i++) Assert.Contains(found, k => k.WordId == $"a{i}");
        Assert.Contains(found, k => k.MeaningId == "meaning" && k.Locale == AudioLocale.EnGb && k.Step == EnrichmentStep.Audio);
        Assert.DoesNotContain(found, k => k.MeaningId == "meaning" && k.Locale == AudioLocale.EnUs);
        Assert.DoesNotContain(found, k => k.WordId == "word" && k.MeaningId == null && k.Locale == AudioLocale.EnGb && k.Step == EnrichmentStep.Audio);
        Assert.NotEmpty((await db.EnrichmentDiscovery.AsNoTracking().SingleAsync(c => c.Id == "words", Ct)).Cursor);
    }

    [Fact]
    public async Task DueQueue_ReservesOldWorkWhileServingNewArrivals_AndNeverReturnsExhausted()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Ct);
        var clock = new FixedClock(); var tasks = new EnrichmentTaskRepository(db, clock);
        db.Vocabularies.Add(new() { Id = "old", Word = "old" }); await db.SaveChangesAsync(Ct); db.ChangeTracker.Clear();
        var old = (await tasks.EnsureAsync(new("old", null, AudioLocale.EnGb, EnrichmentStep.Audio), "v", 1, Ct))!;
        clock.Now = 2000;
        db.Vocabularies.Add(new() { Id = "new", Word = "new" }); await db.SaveChangesAsync(Ct); db.ChangeTracker.Clear();
        var newest = (await tasks.EnsureAsync(new("new", null, AudioLocale.EnGb, EnrichmentStep.Audio), "v", clock.Now, Ct))!;
        var due = await new EnrichmentSchedulerRepository(db).DueAsync(2, clock.Now, Ct);
        Assert.Equal(new[] { newest.Id, old.Id }, due);
        var guard = (await tasks.ClaimAsync(newest.Id, "worker", clock.Now, clock.Now + 1000, Ct))!;
        await tasks.StopAsync(guard, "text.invalid_plan", clock.Now, Ct);
        Assert.DoesNotContain(newest.Id, await new EnrichmentSchedulerRepository(db).DueAsync(2, clock.Now, Ct));
    }

    [Fact]
    public async Task NotificationIsAfterOutermostCommit_RollbackHasNone_AndThrowingNotifierCannotUndoCommit()
    {
        using var file = new AudioTestDatabase(); var notifier = new Notifier();
        await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite($"Data Source={file.PathName};Pooling=False").Options, notifier);
        await db.Database.MigrateAsync(Ct); var uow = new UnitOfWork(db);
        await uow.ExecuteInTransactionAsync(async () =>
        {
            db.Vocabularies.Add(new() { Id = "committed", Word = "committed" }); await uow.SaveChangesAsync(Ct);
            await uow.ExecuteInTransactionAsync(async () => { Assert.Equal(0, notifier.Calls); return 1; }, Ct);
            Assert.Equal(0, notifier.Calls); return 1;
        }, Ct);
        Assert.Equal(1, notifier.Calls); Assert.Equal("committed", Assert.Single(notifier.Words));
        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteInTransactionAsync<int>(async () =>
        {
            db.Vocabularies.Add(new() { Id = "rollback", Word = "rollback" }); await uow.SaveChangesAsync(Ct);
            throw new InvalidOperationException("Synthetic rollback.");
        }, Ct));
        Assert.Equal(1, notifier.Calls); Assert.False(await db.Vocabularies.AnyAsync(w => w.Id == "rollback", Ct));
        db.ChangeTracker.Clear(); notifier.Throw = true;
        db.Vocabularies.Add(new() { Id = "notification-failed", Word = "notification-failed" });
        await uow.SaveChangesAsync(Ct);
        Assert.True(await db.Vocabularies.AnyAsync(w => w.Id == "notification-failed", Ct)); Assert.Equal(2, notifier.Calls);
    }

    [Fact]
    public async Task ThrottleOutcomeAndItemAccounting_CommitTogether_OrRollBackTogetherAcrossRestart()
    {
        using var file = new AudioTestDatabase(); var fault = new FailServiceUpdate(); var clock = new FixedClock();
        EnrichmentServiceAdmission admission; EnrichmentExecutionGuard guard; string callId = Guid.NewGuid().ToString(); string targetId;
        await using (var db = file.Open(fault))
        {
            await db.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(db);
            var tasks = new EnrichmentTaskRepository(db, clock); var scheduler = new EnrichmentSchedulerRepository(db, tasks);
            var task = (await tasks.EnsureAsync(new("word", null, AudioLocale.EnGb, EnrichmentStep.Audio), "v", 1, Ct))!;
            targetId = task.Id; guard = (await tasks.ClaimAsync(targetId, "worker", 1, 100_000, Ct))!;
            await tasks.BeginCallAsync(guard, callId, "tts", Guid.NewGuid().ToString(), 1, Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.FinishCallInTransactionAsync(guard, callId,
                EnrichmentCallResult.Throttled, "tts.rate_limited", 1, 1000, Ct));
            admission = await scheduler.EnterServiceAsync("tts", "v", false, 1, 100_000, Ct);
            fault.Enabled = true;
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => scheduler.FinishServiceAsync(admission, "Throttled", "rate_limited", true,
                1, null, Policy, Ct, new(guard, callId, EnrichmentCallResult.Throttled, "tts.rate_limited", 1000)));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.True(fault.Triggered);
        }
        await using var restarted = file.Open(); var resumedTasks = new EnrichmentTaskRepository(restarted, clock);
        var resumedScheduler = new EnrichmentSchedulerRepository(restarted, resumedTasks);
        Assert.Equal(0, (await resumedScheduler.ServiceAsync("tts", Ct))!.ThrottleSeconds);
        Assert.Equal(admission.LeaseId, (await resumedScheduler.ServiceAsync("tts", Ct))!.LeaseId);
        Assert.Equal(EnrichmentCallResult.Started, Assert.Single((await resumedTasks.GetAsync(targetId, Ct))!.Calls).Result);
        await resumedScheduler.FinishServiceAsync(admission, "Throttled", "rate_limited", true, 1, null, Policy, Ct,
            new(guard, callId, EnrichmentCallResult.Throttled, "tts.rate_limited", 1000));
        var recovered = (await resumedTasks.GetAsync(targetId, Ct))!; var round = Assert.Single(recovered.Rounds);
        Assert.Equal(EnrichmentCallResult.Throttled, Assert.Single(recovered.Calls).Result);
        Assert.Equal(0, round.ConsumedFailures); Assert.Equal(8001, round.NextAttemptAtUnixMs);
        Assert.Equal(round.NextAttemptAtUnixMs, (await resumedScheduler.ServiceAsync("tts", Ct))!.NextAttemptAtUnixMs);
    }

    [Fact]
    public async Task RealImportApprovalAndEditPaths_PublishOnlyCommittedCatalogChanges()
    {
        using var file = new AudioTestDatabase(); var notifier = new Notifier();
        await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite($"Data Source={file.PathName};Pooling=False").Options, notifier);
        await db.Database.MigrateAsync(Ct);
        var uow = new UnitOfWork(db); var words = new VocabularyRepository(db);
        var books = new VocabularyBookRepository(db); var meanings = new VocabularyMeaningRepository(db);
        var units = new VocabularyBookUnitRepository(db); var positions = new VocabularyMeaningUnitRepository(db);
        var settings = new MeaningSubmissionSettings { BookId = "book" }; var submissions = new MeaningSubmissionRepository(db);
        var catalog = new VocabularyDomainService(words, books, meanings, units, positions, uow, settings, submissions);
        db.VocabularyBooks.Add(new() { Id = "book", BookName = "Fixture", Status = true }); await uow.SaveChangesAsync(Ct);
        Assert.Equal(0, notifier.Calls);
        await catalog.ImportBatchAsync("book", [
            (new VocabularyModel { Word = "first" }, new VocabularyMeaningModel { Meaning = "first definition" }, null, null, null),
            (new VocabularyModel { Word = "second" }, new VocabularyMeaningModel { Meaning = "second definition" }, null, null, null)]);
        Assert.Equal(1, notifier.Calls); Assert.Equal(2, notifier.Words.Count);
        var service = new MeaningSubmissionService(submissions, words, meanings, catalog, uow, settings, TimeProvider.System);
        var pending = await service.SubmitAsync("urn:test:client", "proposed", null, "proposed definition", null, Ct);
        Assert.Equal(1, notifier.Calls); // Pending submissions are not catalog work.
        var approved = await service.ApproveAsync(pending.Id, new("proposed", null, "proposed definition", null, null, null), Ct);
        Assert.Equal(2, notifier.Calls); Assert.Single(notifier.Words);
        var wordId = await db.Vocabularies.Where(w => w.Word == "proposed").Select(w => w.Id).SingleAsync(Ct);
        Assert.Equal(wordId, notifier.Words[0]);
        await new VocabularyWordEditService(words, new VocabularyWordEditRepository(db), meanings, positions, uow)
            .ReplaceAsync(wordId, "Proposed", null, null, Ct);
        Assert.Equal(3, notifier.Calls); Assert.Equal(wordId, Assert.Single(notifier.Words));
        var meaningId = await db.VocabularyMeanings.Where(m => m.VocabularyId == wordId).Select(m => m.Id).SingleAsync(Ct);
        await new VocabularyMeaningEditService(words, books, meanings, positions, uow)
            .ReplaceAsync("book", wordId, meaningId, null, "corrected definition", null, Ct);
        Assert.Equal(4, notifier.Calls); Assert.Equal(wordId, Assert.Single(notifier.Words));
        Assert.Equal(MeaningSubmissionStatus.Approved, approved.Status);
    }

    [Fact]
    public async Task SchedulerMigration_PreservesTaskCallsAndCatalog_AndDownLosesOnlySchedulerState()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        const string previous = "20261101194034_AddMeaningPhoneticEpochs";
        await db.GetService<IMigrator>().MigrateAsync(previous, Ct); await AudioTestDatabase.SeedAsync(db);
        var tasks = new EnrichmentTaskRepository(db, new FixedClock());
        var task = (await tasks.EnsureAsync(new("word", null, AudioLocale.EnGb, EnrichmentStep.Audio), "v", 1, Ct))!;
        var guard = (await tasks.ClaimAsync(task.Id, "worker", 1, 5000, Ct))!;
        await tasks.BeginCallAsync(guard, Guid.NewGuid().ToString(), "tts", Guid.NewGuid().ToString(), 1, Ct);
        await tasks.FinishCallAsync(guard, (await tasks.GetAsync(task.Id, Ct))!.Calls[0].Id, EnrichmentCallResult.Throttled, "tts.rate_limited", 1, 100, Ct);
        var before = System.Text.Json.JsonSerializer.Serialize(await tasks.GetAsync(task.Id, Ct));
        await db.Database.MigrateAsync(Ct);
        var repo = new EnrichmentSchedulerRepository(db); await repo.DiscoverAsync(1, [], Ct);
        await repo.EnterServiceAsync("tts", "v", false, 1, 100, Ct);
        await db.GetService<IMigrator>().MigrateAsync(previous, Ct);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await tasks.GetAsync(task.Id, Ct)));
        Assert.Equal(2, await db.Vocabularies.CountAsync(Ct));
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name IN ('enrichment_discovery','enrichment_service','enrichment_service_probe')"));
        await db.Database.MigrateAsync(Ct);
        Assert.Empty(await db.EnrichmentServices.ToListAsync(Ct)); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    private sealed class FixedClock : TimeProvider
    { public long Now = 1; public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now); }
    private sealed class Notifier : IEnrichmentChangeNotifier
    {
        public int Calls; public bool Throw; public IReadOnlyList<string> Words = [];
        public void Notify(IReadOnlyList<string> wordIds) { Calls++; Words = wordIds; if (Throw) throw new InvalidOperationException("Synthetic notifier failure."); }
    }
    private sealed class FailServiceUpdate : DbCommandInterceptor
    {
        public bool Enabled; public bool Triggered;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("UPDATE \"enrichment_service\"", StringComparison.Ordinal))
            { Triggered = true; throw new InvalidOperationException("Synthetic service commit failure."); }
            return ValueTask.FromResult(result);
        }
    }
}
