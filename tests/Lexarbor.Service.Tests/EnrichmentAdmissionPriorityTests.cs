using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Host;
using Lexarbor.Host.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Fact]
    public async Task Worker_QueuedExpansionYieldsToPreparationForTheSameWordOnRefill()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Publish(new() { ["Enrichment:DiscoveryBatchSize"] = "1" });
        f.Db.Vocabularies.Add(new() { Id = "walk", Word = "walk" });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        await f.Ensure(new("walk", null, AudioLocale.EnGb, EnrichmentStep.Prepare));
        f.Clock.Now += 100;
        await f.Ensure(ExpansionKey);
        await f.Ensure(Key(EnrichmentStep.Prepare));
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new BatchHandler(false)
        {
            ObserveKind = (word, expansion) =>
        {
            if (word == "lead") observed.TrySetResult(expansion);
        }
        };
        using var clients = new HttpFactory(handler);
        await using var provider = PipelineProvider(f, clients);
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, new(), f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        await worker.StartAsync(Ct);
        try { Assert.False(await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)); }
        finally { await worker.StopAsync(Ct); }
    }

    [Fact]
    public async Task Worker_ResumesImmediatelyAfterSuccessfulProbe_WithoutWaitingForCachedPauseDeadline()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Publish(new() { ["Enrichment:ServiceFailureThreshold"] = "1" });
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var gate = new EnrichmentServiceGate(scheduler, f.Runtime, f.Clock);
        var failed = await gate.EnterAsync("text", false, Ct);
        await gate.FinishAsync(failed, "ServiceFailure", "fixture", true, null, Ct);
        f.Clock.Now = (await scheduler.ServiceAsync(failed.ServiceId, Ct))!.NextAttemptAtUnixMs;
        await f.Ensure(Key());
        using var handler = new BatchHandler(false); using var clients = new HttpFactory(handler);
        await using var provider = PipelineProvider(f, clients);
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, new(), f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        await worker.RunBatchAsync(Ct);
        Assert.False((await scheduler.ServiceAsync(failed.ServiceId, Ct))!.Paused);
        Assert.True(handler.Calls >= 2); Assert.True(f.Http.SpeechCalls > 0);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(401)]
    public async Task Batch_ServiceFailureKeepsUnsentMembersInTheTextLane(int status)
    {
        await using var f = await Fixture.Create(shared: true);
        var ids = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var word = "prepare-" + i;
            f.Db.Vocabularies.Add(new() { Id = word, Word = word });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            ids.Add((await f.Ensure(new(word, null, AudioLocale.EnGb, EnrichmentStep.Prepare))).Id);
        }
        using var handler = new UnavailableBatchHandler(status); using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        var result = await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).DispatchAsync(ids, Ct);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, result.Requests);
        Assert.Equal(6, result.Ready.Count); Assert.Equal(ids[^1], Assert.Single(result.Deferred));
        var unsent = (await f.Tasks.GetAsync(ids[^1], Ct))!;
        Assert.Equal(EnrichmentState.Pending, Round(unsent).State);
        Assert.Empty(unsent.Calls); Assert.Equal(0, Round(unsent).RequestCount);
        Assert.Empty(await f.Db.EnrichmentServiceLeases.ToArrayAsync(Ct));
    }

    private sealed class UnavailableBatchHandler(int status) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)); }
    }

    [Fact]
    public async Task Worker_ReadyAudioProgressesWhileTextCapacityIsHeld()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Run(Key(EnrichmentStep.Prepare));
        var ready = await f.Ensure(Key());
        f.Db.Vocabularies.Add(new() { Id = "pending", Word = "pending" });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var pending = await f.Ensure(new("pending", null, AudioLocale.EnGb, EnrichmentStep.Audio));
        var gate = new EnrichmentServiceGate(new EnrichmentSchedulerRepository(f.Db, f.Tasks), f.Runtime, f.Clock);
        var held = await gate.EnterAsync("text", false, Ct);
        using var handler = new BatchHandler(false); using var clients = new HttpFactory(handler);
        await using var provider = PipelineProvider(f, clients);
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, new(), f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        await worker.RunBatchAsync(Ct, discover: false);
        Assert.Equal(1, f.Http.SpeechCalls); Assert.Equal(0, handler.Calls);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Tasks.GetAsync(ready.Id, Ct))!).State);
        Assert.Equal(EnrichmentState.Pending, Round((await f.Tasks.GetAsync(pending.Id, Ct))!).State);
        Assert.Empty(await f.Db.EnrichmentWaits.ToArrayAsync(Ct));
        await gate.FinishAsync(held, "Superseded", null, false, null, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_LegacyLeaseDefersWithoutChangingTargets_ThenResumesAfterReleaseOrExpiry(bool expire)
    {
        await using var f = await Fixture.Create(shared: true);
        var target = await f.Ensure(Key());
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var identity = EnrichmentServiceGate.Identity("text", f.Runtime.Current);
        var deadline = f.Clock.Now + 360_000;
        var lease = await scheduler.EnterServiceAsync(identity, f.Runtime.Current.TextVersion!, false, f.Clock.Now, deadline, Ct);
        // Simulate the scalar lease retained from a legacy worker, without a lease-table row.
        await f.Db.EnrichmentServiceLeases.ExecuteDeleteAsync(Ct);
        using var handler = new BatchHandler(false); using var clients = new HttpFactory(handler);
        await using var provider = PipelineProvider(f, clients);
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, new(), f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        for (var i = 0; i < 30; i++)
        {
            await worker.RunBatchAsync(Ct, discover: false);
            f.Clock.Now += 1_000;
        }
        Assert.Equal(0, handler.Calls); Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
        Assert.Equal(1, await f.Db.EnrichmentTargets.CountAsync(Ct));
        Assert.Empty(await f.Db.EnrichmentWaits.ToArrayAsync(Ct));
        Assert.Empty(await f.Db.EnrichmentCalls.ToArrayAsync(Ct));
        Assert.Empty(await f.Db.AiRequestBatches.ToArrayAsync(Ct));
        var deferred = (await f.Tasks.GetAsync(target.Id, Ct))!;
        Assert.Equal(EnrichmentState.Pending, Round(deferred).State);
        Assert.Equal(Round(target).UpdatedAtUnixMs, Round(deferred).UpdatedAtUnixMs);
        Assert.Equal(lease.LeaseId, (await scheduler.ServiceAsync(identity, Ct))!.LeaseId);
        if (expire) f.Clock.Now = deadline + 1;
        else
        {
            await new EnrichmentServiceGate(scheduler, f.Runtime, f.Clock).FinishAsync(lease, "Superseded", null, false, null, Ct);
            f.Clock.Now += 5_000;
        }
        await worker.RunBatchAsync(Ct, discover: false);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, f.Http.SpeechCalls);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Tasks.GetAsync(target.Id, Ct))!).State);
    }

    [Fact]
    public async Task Availability_CountsSlotsWithoutDoubleCountingTheScalarPointer_AndHonorsCooldown()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Publish(new() { ["Enrichment:TextConcurrency"] = "2" });
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var gate = new EnrichmentServiceGate(scheduler, f.Runtime, f.Clock);
        Assert.Equal(2, (await gate.AvailabilityAsync("text", Ct)).AvailableSlots);
        var first = await gate.EnterAsync("text", false, Ct);
        Assert.Equal(1, (await gate.AvailabilityAsync("text", Ct)).AvailableSlots);
        var second = await gate.EnterAsync("text", false, Ct);
        var full = await gate.AvailabilityAsync("text", Ct);
        Assert.Equal(0, full.AvailableSlots); Assert.Equal(f.Clock.Now + 5_000, full.NextCheckAtUnixMs);
        await gate.FinishAsync(first, "Throttled", "batch.throttled", true, TimeSpan.FromSeconds(60), Ct);
        await gate.FinishAsync(second, "Ready", null, true, null, Ct);
        var cooling = await gate.AvailabilityAsync("text", Ct);
        Assert.Equal(0, cooling.AvailableSlots); Assert.True(cooling.NextCheckAtUnixMs >= f.Clock.Now + 60_000);
        f.Clock.Now = cooling.NextCheckAtUnixMs;
        Assert.Equal(2, (await gate.AvailabilityAsync("text", Ct)).AvailableSlots);
    }

    [Fact]
    public async Task Batch_LosingAdmissionDefersOriginalTargets_WithoutEnteringThePublicationLane()
    {
        await using var f = await Fixture.Create(shared: true);
        var target = await f.Ensure(Key(EnrichmentStep.Fields));
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var gate = new EnrichmentServiceGate(scheduler, f.Runtime, f.Clock);
        var held = await gate.EnterAsync("text", false, Ct);
        using var handler = new BatchHandler(false); using var clients = new HttpFactory(handler);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        var executor = BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients);
        var deferred = await executor.DispatchAsync([target.Id], Ct);
        Assert.Empty(deferred.Ready); Assert.Equal(target.Id, Assert.Single(deferred.Deferred));
        Assert.Equal(0, deferred.Requests); Assert.True(deferred.NextCheckAtUnixMs >= f.Clock.Now + 5_000);
        Assert.Equal(0, handler.Calls); Assert.Empty(await f.Db.EnrichmentWaits.ToArrayAsync(Ct));
        Assert.Empty(await f.Db.EnrichmentCalls.ToArrayAsync(Ct));
        await gate.FinishAsync(held, "Superseded", null, false, null, Ct);
        var ready = await executor.DispatchAsync([target.Id], Ct);
        Assert.Equal(target.Id, Assert.Single(ready.Ready)); Assert.Empty(ready.Deferred);
        Assert.Equal(1, ready.Requests); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Planner_ReservesPreparationEvenBehindOlderExpansionBacklog()
    {
        await using var f = await Fixture.Create();
        for (var i = 0; i < 8; i++)
        {
            var word = "expansion-" + i; var meaning = "sense-" + i;
            f.Db.Vocabularies.Add(new() { Id = word, Word = word });
            f.Db.VocabularyMeanings.Add(new() { Id = meaning, VocabularyId = word, BookId = "book", Meaning = "fixture" });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            await f.Ensure(new(word, meaning, null, EnrichmentStep.Expand));
        }
        f.Clock.Now += 100;
        for (var i = 0; i < 2; i++)
        {
            var word = "preparation-" + i;
            f.Db.Vocabularies.Add(new() { Id = word, Word = word });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            await f.Ensure(new(word, null, AudioLocale.EnGb, EnrichmentStep.Prepare));
        }
        var plan = await new EnrichmentSchedulerRepository(f.Db).PlanAsync(2, f.Clock.Now, [], Ct);
        Assert.Equal(2, plan.Count(i => i.Lane == "Text"));
        Assert.Equal(2, plan.Count(i => i.Lane == "Expansion"));
    }

    [Fact]
    public async Task Worker_ProcessesThreePreparationRequestsBeforeExpansion_WhenBothAreQueued()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Ensure(ExpansionKey);
        f.Clock.Now += 100;
        for (var i = 0; i < 8; i++)
        {
            var word = "prepare" + i;
            f.Db.Vocabularies.Add(new() { Id = word, Word = word });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            await f.Ensure(new(word, null, AudioLocale.EnGb, EnrichmentStep.Prepare));
        }
        f.Db.AiRequestBatches.Add(new()
        {
            Id = "small",
            Kind = "Prepare",
            ConfigurationVersion = f.Runtime.Current.TextVersion!,
            StartedAtUnixMs = 1,
            FinishedAtUnixMs = 2,
            Outcome = "Ready",
            NextSize = 1
        });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var requested = new List<string>();
        using var handler = new BatchHandler(false) { BeforeSend = (word, _) => { requested.Add(word); return Task.CompletedTask; } };
        using var clients = new HttpFactory(handler);
        await using var provider = PipelineProvider(f, clients);
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, new(), f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        await worker.RunBatchAsync(Ct, discover: false);
        Assert.Equal(3, requested.IndexOf("lead"));
        Assert.Equal(1, requested.Count(w => w == "lead"));
    }

    [Fact]
    public void TextPolicy_PaysForSlowExpansion_WithPreparationTime_AndKeepsOnlyOneExpansionInFlight()
    {
        var policy = new EnrichmentTextSchedulingPolicy();
        Assert.False(policy.CanExpand(preparationPending: true, expansionRunning: false));
        for (var i = 0; i < 3; i++) policy.Complete(expansion: false, requests: 1, requestMs: 20_000);
        Assert.True(policy.CanExpand(true, false)); Assert.False(policy.CanExpand(true, true));
        policy.Complete(expansion: true, requests: 1, requestMs: 120_000);
        for (var i = 0; i < 3; i++) policy.Complete(expansion: false, requests: 1, requestMs: 20_000);
        Assert.False(policy.CanExpand(true, false));
        policy.Complete(expansion: false, requests: 1, requestMs: 300_000);
        Assert.True(policy.CanExpand(true, false));
        policy.Complete(expansion: true, requests: 0, requestMs: 0); // Admission failure does not spend a turn.
        Assert.True(policy.CanExpand(true, false));
        Assert.True(policy.CanExpand(false, false)); Assert.False(policy.CanExpand(false, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleTargetCancellation_ReleasesServiceCapacity_WhilePreservingUnknownCallHistory(bool audio)
    {
        await using var f = await Fixture.Create(shared: true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        if (audio) f.Http.BeforeSpeechResponse = _ => { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        else f.Http.BeforeTextResponse = () => { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var target = await f.Ensure(Key(audio ? EnrichmentStep.Audio : EnrichmentStep.Prepare));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Executor.ExecuteAsync(target.Id, "cancel", stop.Token));
        await using var db = f.Open();
        Assert.Empty(await db.EnrichmentServiceLeases.ToArrayAsync(Ct));
        Assert.Contains(await db.EnrichmentCalls.AsNoTracking().ToArrayAsync(Ct), c => c.Result == EnrichmentCallResult.Started);
        var gate = new EnrichmentServiceGate(new EnrichmentSchedulerRepository(db), f.Runtime, f.Clock);
        Assert.Equal(1, (await gate.AvailabilityAsync(audio ? "tts" : "text", Ct)).AvailableSlots);
    }

    [Fact]
    public async Task ExpansionCancellation_ReleasesServiceCapacity()
    {
        await using var f = await Fixture.Create(shared: true); await Supplement(f);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var handler = new ExpansionHandler([]) { Before = () => { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        using var clients = new HttpFactory(handler);
        var target = await f.Ensure(ExpansionKey);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Expansion(f, f.Db, clients).ExecuteAsync(target, "cancel", stop.Token));
        await using var db = f.Open();
        Assert.Empty(await db.EnrichmentServiceLeases.ToArrayAsync(Ct));
        Assert.Equal(EnrichmentCallResult.Started, Assert.Single(await db.EnrichmentCalls.ToArrayAsync(Ct)).Result);
    }

    [Fact]
    public async Task ProbeCancellation_ReleasesLeaseWithoutHealingThePausedService()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Publish(new() { ["Enrichment:ServiceFailureThreshold"] = "1" });
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var gate = new EnrichmentServiceGate(scheduler, f.Runtime, f.Clock);
        var failed = await gate.EnterAsync("text", false, Ct);
        await gate.FinishAsync(failed, "ServiceFailure", "fixture", true, null, Ct);
        f.Clock.Now = (await scheduler.ServiceAsync(failed.ServiceId, Ct))!.NextAttemptAtUnixMs;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Http.BeforeTextResponse = () => { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        using var clients = new HttpFactory(f.Http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ProbeAsync("text",
            new OpenAiCompatiblePronunciationPreparationClient(clients, f.Runtime, f.Clock),
            new PronunciationSpeechClient(clients, f.Runtime, f.Clock), stop.Token));
        var status = (await scheduler.ServiceAsync(failed.ServiceId, Ct))!;
        Assert.True(status.Paused); Assert.Null(status.LeaseId); Assert.Equal(1, status.ConsecutiveFailures);
        Assert.True(status.NextAttemptAtUnixMs > f.Clock.Now);
        var probe = Assert.Single(await scheduler.ProbesAsync(failed.ServiceId, Ct));
        Assert.Equal("Unknown", probe.Result); Assert.True(probe.RequestMade);
    }
}
