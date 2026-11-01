using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Integration;
using Lexarbor.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Fact]
    public async Task Worker_LostNotificationDiscoversExplicitTargets_StorageWaits_ThenPublishesOnlyMissingSides()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        var services = new ServiceCollection();
        services.AddScoped(_ => f.Open());
        services.AddScoped<IEnrichmentSchedulerRepository>(p => new EnrichmentSchedulerRepository(p.GetRequiredService<VocabularyDbContext>()));
        services.AddScoped<IEnrichmentTaskRepository>(p => new EnrichmentTaskRepository(p.GetRequiredService<VocabularyDbContext>(), f.Clock));
        services.AddScoped(p => new EnrichmentServiceGate(p.GetRequiredService<IEnrichmentSchedulerRepository>(), f.Runtime, f.Clock));
        services.AddScoped(p => f.ExecutorFor(p.GetRequiredService<VocabularyDbContext>(),
            (EnrichmentTaskRepository)p.GetRequiredService<IEnrichmentTaskRepository>()));
        using var clients = new HttpFactory(f.Http);
        services.AddSingleton<IPronunciationPreparationClient>(new OpenAiCompatiblePronunciationPreparationClient(clients, f.Runtime, f.Clock));
        services.AddSingleton<IPronunciationSpeechClient>(new PronunciationSpeechClient(clients, f.Runtime, f.Clock));
        services.AddSingleton<IAudioUploadOperationFactory>(f.Operations);
        await using var provider = services.BuildServiceProvider();
        var notifications = new EnrichmentNotifications();
        var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, notifications,
            f.Clock, NullLogger<EnrichmentWorker>.Instance);
        f.Operations.Enabled = false;
        await worker.RunBatchAsync(Ct);
        Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
        Assert.Equal(2, await f.Db.EnrichmentRounds.CountAsync(r => r.WaitReason == EnrichmentWaitReason.StorageDisabled, Ct));
        f.Clock.Now += 30_000; f.Operations.Enabled = true;
        await worker.RunBatchAsync(Ct);
        Assert.Equal(2, f.Http.TextCalls); Assert.Equal(2, f.Http.SpeechCalls); Assert.Equal(2, f.Store.Puts);
        Assert.Equal(2, await f.Db.VocabularyAudioBindings.CountAsync(Ct));
        // This edit creates a real persistent R03 task. No notification is
        // delivered; the worker must still find the specific own target.
        var tasks = new EnrichmentTaskRepository(f.Db, f.Clock);
        var edits = new MeaningPhoneticRepository(f.Db, new AudioInvalidationRepository(f.Db, f.Clock), tasks);
        var before = await edits.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Ct);
        await new MeaningPhoneticEditService(edits, new UnitOfWork(f.Db)).ApplyAsync(
            [new("book", "word", "meaning", AudioLocale.EnGb, true, "liːd", before)],
            new(f.Runtime.Current.TextVersion, f.Runtime.Current.UkTtsVersion, f.Runtime.Current.UsTtsVersion), f.Clock.Now, Ct);
        f.Clock.Now += 30_000;
        await worker.RunBatchAsync(Ct);
        Assert.Equal(3, f.Http.SpeechCalls); Assert.Equal(3, f.Store.Puts);
        Assert.Equal(3, await f.Db.VocabularyAudioBindings.CountAsync(Ct));
        Assert.False(await f.Db.EnrichmentTargets.AnyAsync(t => t.MeaningId == "meaning" && t.Locale == AudioLocale.EnUs, Ct));
        before = await edits.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Ct);
        await new MeaningPhoneticEditService(edits, new UnitOfWork(f.Db)).ApplyAsync(
            [new("book", "word", "meaning", AudioLocale.EnGb, true, null, before)],
            new(f.Runtime.Current.TextVersion, f.Runtime.Current.UkTtsVersion, f.Runtime.Current.UsTtsVersion), f.Clock.Now, Ct);
        f.Clock.Now += 30_000;
        await worker.RunBatchAsync(Ct);
        Assert.Equal(3, f.Http.SpeechCalls); Assert.Equal(2, await f.Db.VocabularyAudioBindings.CountAsync(Ct));
        var specific = await f.Db.EnrichmentTargets.AsNoTracking().Where(t => t.MeaningId == "meaning").ToArrayAsync(Ct);
        foreach (var target in specific)
            Assert.DoesNotContain(await f.Db.EnrichmentRounds.AsNoTracking().Where(r => r.TargetId == target.Id && r.Number == target.CurrentRound).ToArrayAsync(Ct),
                r => r.State is EnrichmentState.Pending or EnrichmentState.Running or EnrichmentState.Waiting);
    }

    [Fact]
    public async Task DisabledAndMissingSide_ResumeSameRoundAfterRepair_WithoutAffectingSuccessfulAccent()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        await f.Publish(new() { ["Enrichment:AudioEnabled"] = "false" });
        var uk = (await f.Run(Key()))!;
        Assert.Equal(EnrichmentWaitReason.Disabled, Round(uk).WaitReason);
        Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
        await f.Publish(new() { ["Enrichment:AudioEnabled"] = "true", ["Enrichment:Tts:Us:Voice"] = "" });
        f.Clock.Now = Round(uk).NextAttemptAtUnixMs!.Value;
        uk = (await f.Run(Key()))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(uk).State); Assert.Equal(1, uk.CurrentRound);
        var us = (await f.Run(Key(locale: AudioLocale.EnUs)))!;
        Assert.Equal(EnrichmentWaitReason.ConfigurationMissing, Round(us).WaitReason);
        Assert.Equal(0, Round(us).ConsumedFailures); Assert.Equal(0, Round(us).RequestCount);
        await f.Publish(new() { ["Enrichment:Tts:Us:Voice"] = "us" });
        us = (await f.Run(Key(locale: AudioLocale.EnUs)))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(us).State);
        Assert.Equal(2, f.Http.SpeechCalls); Assert.Equal(2, f.Store.Puts);
        uk = (await f.Run(Key()))!;
        Assert.Equal(1, uk.CurrentRound); Assert.Equal(1, Round(uk).RequestCount); Assert.Equal(2, f.Http.SpeechCalls);
    }

    [Fact]
    public async Task SharedThrottle_SevenActualRequestsDoNotExhaust_UsWaitsWithoutDispatch_AndRestartPreservesDelay()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        f.Http.SpeechOutcome = "throttle";
        var uk = (await f.Run(Key()))!;
        var us = (await f.Run(Key(locale: AudioLocale.EnUs)))!;
        Assert.Equal(1, f.Http.SpeechCalls); Assert.Equal(0, Round(us).RequestCount);
        Assert.Equal(EnrichmentWaitReason.Throttled, Round(us).WaitReason);
        for (var i = 1; i < 7; i++)
        {
            f.Clock.Now = Round(uk).NextAttemptAtUnixMs!.Value;
            await using var db = f.Open(); var tasks = new EnrichmentTaskRepository(db, f.Clock);
            uk = (await f.ExecutorFor(db, tasks).ExecuteAsync(uk.Id, "restart", Ct))!;
            Assert.Equal(0, Round(uk).ConsumedFailures); Assert.Equal(1, uk.CurrentRound);
            Assert.Equal(i + 1, Round(uk).RequestCount);
        }
        await using (var db = f.Open())
        {
            var state = (await new EnrichmentSchedulerRepository(db).ServiceAsync(EnrichmentServiceGate.Identity("tts", f.Runtime.Current), Ct))!;
            Assert.Equal(120, state.ThrottleSeconds); Assert.False(state.Paused);
            Assert.Equal(Round(uk).NextAttemptAtUnixMs, state.NextAttemptAtUnixMs);
            var tasks = new EnrichmentTaskRepository(db, f.Clock);
            await f.ExecutorFor(db, tasks).ExecuteAsync(us.Id, "still-waiting", Ct);
            Assert.Equal(7, f.Http.SpeechCalls);
        }
        f.Clock.Now = Round(uk).NextAttemptAtUnixMs!.Value; f.Http.SpeechOutcome = "ready";
        uk = (await f.Executor.ExecuteAsync(uk.Id, "recovered", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(uk).State); Assert.Equal(8, Round(uk).RequestCount);
        Assert.Equal(0, Round(uk).ConsumedFailures); Assert.Equal(1, f.Store.Puts);
    }

    [Fact]
    public async Task FifthFailurePausesService_ProbeDoesNotSpendItemBudget_RecoveryDoesNotReviveExhaustedSide()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        f.Http.SpeechOutcome = "service";
        EnrichmentTask? failed = null;
        for (var i = 0; i < 5; i++)
        {
            if (failed != null) f.Clock.Now = Round(failed).NextAttemptAtUnixMs!.Value;
            failed = (await f.Run(Key()))!;
        }
        Assert.Equal(EnrichmentState.Failed, Round(failed!).State);
        Assert.Equal(5, Round(failed!).ConsumedFailures); Assert.Equal(5, Round(failed!).RequestCount);
        var repository = new EnrichmentSchedulerRepository(f.Db);
        var serviceId = EnrichmentServiceGate.Identity("tts", f.Runtime.Current);
        var state = (await repository.ServiceAsync(serviceId, Ct))!;
        Assert.True(state.Paused); Assert.Equal(60, state.PauseSeconds);
        var waiting = (await f.Run(Key(locale: AudioLocale.EnUs)))!;
        Assert.Equal(EnrichmentWaitReason.ServicePaused, Round(waiting).WaitReason);
        Assert.Equal(0, Round(waiting).ConsumedFailures); Assert.Equal(0, Round(waiting).RequestCount);
        Assert.Equal(5, f.Http.SpeechCalls);
        // A reachable text service remains independently usable while TTS pauses.
        Assert.False((await repository.ServiceAsync(EnrichmentServiceGate.Identity("text", f.Runtime.Current), Ct))!.Paused);
        f.Clock.Now = state.NextAttemptAtUnixMs; f.Http.SpeechOutcome = "ready";
        await using var resumedDb = f.Open();
        var gate = new EnrichmentServiceGate(new EnrichmentSchedulerRepository(resumedDb, new EnrichmentTaskRepository(resumedDb, f.Clock)), f.Runtime, f.Clock, f.Admission);
        using var clients = new HttpFactory(f.Http);
        await gate.ProbeAsync("tts", new OpenAiCompatiblePronunciationPreparationClient(clients, f.Runtime, f.Clock),
            new PronunciationSpeechClient(clients, f.Runtime, f.Clock), Ct);
        Assert.Equal(6, f.Http.SpeechCalls); Assert.Equal(0, f.Store.Puts);
        Assert.False((await repository.ServiceAsync(serviceId, Ct))!.Paused);
        Assert.True(Assert.Single(await repository.ProbesAsync(serviceId, Ct)).RequestMade);
        var stillFailed = (await f.Executor.ExecuteAsync(failed!.Id, "after-probe", Ct))!;
        Assert.Equal(EnrichmentState.Failed, Round(stillFailed).State); Assert.Equal(5, Round(stillFailed).ConsumedFailures);
        Assert.Equal(5, Round(stillFailed).RequestCount); Assert.Single(stillFailed.Rounds);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Executor.ExecuteAsync(waiting.Id, "continue", Ct))!).State);
        Assert.Equal(7, f.Http.SpeechCalls); Assert.Equal(1, f.Store.Puts);
    }

    [Fact]
    public async Task LocalAdmissionStorageAndInvalidAudio_AreNotSharedProviderOutages()
    {
        await using var f = await Fixture.Create(fields: false, shared: true, capacity: 1);
        using (await f.Admission.EnterAsync(Ct))
        {
            var local = (await f.Run(Key()))!;
            Assert.Equal(EnrichmentWaitReason.Throttled, Round(local).WaitReason);
            Assert.Equal(0, Round(local).RequestCount); Assert.Equal(0, f.Http.SpeechCalls);
            Assert.Null(await new EnrichmentSchedulerRepository(f.Db).ServiceAsync(EnrichmentServiceGate.Identity("tts", f.Runtime.Current), Ct));
            f.Clock.Now = Round(local).NextAttemptAtUnixMs!.Value;
        }
        f.Http.AudioBytes = new byte[500];
        var invalid = (await f.Run(Key()))!;
        Assert.Equal(1, Round(invalid).ConsumedFailures);
        Assert.Equal(0, (await new EnrichmentSchedulerRepository(f.Db).ServiceAsync(EnrichmentServiceGate.Identity("tts", f.Runtime.Current), Ct))!.ConsecutiveFailures);
        f.Clock.Now = Round(invalid).NextAttemptAtUnixMs!.Value; f.Http.AudioBytes = null; f.Store.LosePutResponse = true;
        var publication = (await f.Run(Key()))!;
        Assert.Equal("audio.publication_failed", Round(publication).ErrorCode);
        Assert.Equal(0, (await new EnrichmentSchedulerRepository(f.Db).ServiceAsync(EnrichmentServiceGate.Identity("tts", f.Runtime.Current), Ct))!.ConsecutiveFailures);
        Assert.False((await new EnrichmentSchedulerRepository(f.Db).ServiceAsync(EnrichmentServiceGate.Identity("tts", f.Runtime.Current), Ct))!.Paused);
    }

    [Fact]
    public async Task StableServiceIdentityAndVersion_ShareAccents_KeepReviewSeparate_AndIgnoreVoiceSwitches()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        Assert.Equal(EnrichmentServiceGate.Identity("enrichment.tts", "HTTPS://PROVIDER.TEST:443/v1/", "MiniMaxNative"),
            EnrichmentServiceGate.Identity("enrichment.tts", "https://provider.test/v1", "minimaxnative"));
        Assert.NotEqual(EnrichmentServiceGate.Identity("enrichment.text", "https://provider.test/v1", "OpenAICompatible"),
            EnrichmentServiceGate.Identity("review.text", "https://provider.test/v1", "OpenAICompatible"));
        var serviceVersion = f.Runtime.Current.TtsServiceVersion;
        var uk = EnrichmentTargetExecutor.ConfigurationVersion(Key(), f.Runtime.Current);
        await f.Publish(new() { ["Enrichment:Tts:Us:Voice"] = "changed" });
        Assert.Equal(serviceVersion, f.Runtime.Current.TtsServiceVersion);
        Assert.Equal(uk, EnrichmentTargetExecutor.ConfigurationVersion(Key(), f.Runtime.Current));
        await f.Publish(new() { ["Enrichment:AudioEnabled"] = "false", ["Enrichment:FieldsEnabled"] = "true" });
        Assert.Equal(serviceVersion, f.Runtime.Current.TtsServiceVersion);
        await f.Publish(new() { ["Enrichment:Tts:ApiKey"] = "synthetic-corrected-speech" });
        Assert.NotEqual(serviceVersion, f.Runtime.Current.TtsServiceVersion);
    }

    [Fact]
    public async Task SharedGateCancellation_LeavesLeaseForRestart_WithoutInventingFailure()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        using var cancel = new CancellationTokenSource();
        f.Http.BeforeSpeechResponse = async token => { cancel.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var task = await f.Ensure(Key());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Executor.ExecuteAsync(task.Id, "cancel", cancel.Token));
        await using var restarted = f.Open();
        var state = (await new EnrichmentSchedulerRepository(restarted).ServiceAsync(EnrichmentServiceGate.Identity("tts", f.Runtime.Current), Ct))!;
        Assert.NotNull(state.LeaseId); Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(0, Round((await new EnrichmentTaskRepository(restarted, f.Clock).GetAsync(task.Id, Ct))!).ConsumedFailures);
    }
}
