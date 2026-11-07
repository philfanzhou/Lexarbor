using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Integration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Fact]
    public async Task Planner_PrerequisiteLookupsUseBothPartialIndexes_WithoutPerTargetTableScans()
    {
        await using var f = await Fixture.Create();
        await f.Ensure(Key());
        var capture = new DispatchQueryCapture();
        await using var db = f.Open(capture);
        await new EnrichmentSchedulerRepository(db).PlanAsync(20, f.Clock.Now, [], Ct);
        Assert.NotNull(capture.Sql);
        await db.Database.OpenConnectionAsync(Ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + capture.Sql;
        command.Parameters.AddRange(capture.Parameters);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var plan = new List<string>();
        while (await reader.ReadAsync(Ct)) plan.Add(reader.GetString(3));
        Assert.Contains(plan, p => p.Contains("USING INDEX IX_enrichment_target_word_id_locale_step", StringComparison.Ordinal));
        Assert.Contains(plan, p => p.Contains("USING INDEX IX_enrichment_target_word_id_meaning_id_locale_step", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, p => p.Contains("SCAN pt", StringComparison.Ordinal));
    }

    private sealed class DispatchQueryCapture : DbCommandInterceptor
    {
        public string? Sql;
        public SqliteParameter[] Parameters = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WITH recent AS", StringComparison.Ordinal))
            {
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<DbParameter>().Select(p => new SqliteParameter(p.ParameterName, p.Value)).ToArray();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Planner_ReservesRecentAndHistoricalWords_BeforeRefillingExcludedWork()
    {
        await using var f = await Fixture.Create();
        foreach (var id in Enumerable.Range(0, 8).Select(i => "source-" + i))
        {
            f.Db.Vocabularies.Add(new() { Id = id, Word = id });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            f.Clock.Now += 100;
            await f.Ensure(new(id, null, AudioLocale.EnGb, EnrichmentStep.Prepare));
            await f.Ensure(new(id, null, AudioLocale.EnUs, EnrichmentStep.Prepare));
            await f.Ensure(new(id, null, AudioLocale.EnGb, EnrichmentStep.Fields));
            await f.Ensure(new(id, null, AudioLocale.EnGb, EnrichmentStep.Audio));
        }
        var scheduler = new EnrichmentSchedulerRepository(f.Db);
        var first = await scheduler.PlanAsync(4, f.Clock.Now, [], Ct);
        Assert.Equal(4, first.Count);
        Assert.Equal(new[] { "source-0", "source-1", "source-4", "source-5" }, first.Select(i => i.WordId).Order().ToArray());
        var next = await scheduler.PlanAsync(4, f.Clock.Now, first.Select(i => i.WordId).ToArray(), Ct);
        Assert.Equal(new[] { "source-2", "source-3", "source-6", "source-7" }, next.Select(i => i.WordId).Order().ToArray());
    }

    [Fact]
    public async Task Planner_DeduplicatesBeforeLimit_SkipsBlockedDependencies_AndReservesRecovery()
    {
        await using var f = await Fixture.Create();
        var prepare = await f.Ensure(Key(EnrichmentStep.Prepare));
        var fields = await f.Ensure(Key(EnrichmentStep.Fields));
        var audio = await f.Ensure(Key());
        var guard = (await f.Tasks.ClaimAsync(prepare.Id, "busy", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        await f.Tasks.WaitAsync(guard, EnrichmentWaitReason.Throttled, f.Clock.Now, f.Clock.Now + 60000, Ct);
        foreach (var id in new[] { "a", "b", "crashed" })
        {
            f.Db.Vocabularies.Add(new() { Id = id, Word = id });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            var target = await f.Ensure(new(id, null, AudioLocale.EnGb, EnrichmentStep.Prepare));
            await f.Ensure(new(id, null, AudioLocale.EnUs, EnrichmentStep.Prepare));
            if (id == "crashed")
            {
                var owned = (await f.Tasks.ClaimAsync(target.Id, "crashed", f.Clock.Now, f.Clock.Now + 1, Ct))!;
                await f.Tasks.BeginCallAsync(owned, Guid.NewGuid().ToString(), "text", null, f.Clock.Now, Ct);
            }
        }
        f.Clock.Now += 2;
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var plan = await scheduler.PlanAsync(2, f.Clock.Now, ["b"], Ct);
        Assert.DoesNotContain(plan, i => i.Id == fields.Id || i.Id == audio.Id || i.WordId == "b");
        Assert.Contains(plan, i => i.WordId == "crashed" && i.Lane == "Recovery");
        var text = plan.Where(i => i.Lane == "Text").ToArray();
        Assert.Equal(2, text.Length);
        Assert.Equal(2, text.Select(i => i.WordId).Distinct().Count());
        await using var restarted = f.Open();
        Assert.Contains(await new EnrichmentSchedulerRepository(restarted).PlanAsync(2, f.Clock.Now, [], Ct), i => i.Lane == "Recovery");
    }

    [Fact]
    public async Task Pipeline_TwoTextRequestsAndReadyAudioProgressBeforeSlowSiblingFinishes()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Publish(new() { ["Enrichment:TextConcurrency"] = "2" });
        foreach (var id in new[] { "walk", "jump" })
            f.Db.Vocabularies.Add(new() { Id = id, Word = id });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        await f.Ensure(Key());
        await f.Ensure(Key(locale: AudioLocale.EnUs)); // Same word must not enter another lane.
        f.Clock.Now += 100; // The deliberately slow word is selected before the fast words.
        foreach (var id in new[] { "walk", "jump" }) await f.Ensure(new(id, null, AudioLocale.EnGb, EnrichmentStep.Audio));
        f.Db.AiRequestBatches.Add(new()
        {
            Id = "single",
            Kind = "Prepare",
            ConfigurationVersion = f.Runtime.Current.TextVersion!,
            StartedAtUnixMs = 1,
            FinishedAtUnixMs = 2,
            Outcome = "Ready",
            NextSize = 1
        });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRequests = 0; var slowRequests = 0;
        using var handler = new BatchHandler(false)
        {
            BeforeSend = async (word, token) =>
            {
                if (word == "lead")
                { Interlocked.Increment(ref slowRequests); slowEntered.TrySetResult(); await release.Task.WaitAsync(token); }
                else Interlocked.Increment(ref fastRequests);
            }
        };
        f.Http.BeforeSpeechResponse = _ => { fastAudio.TrySetResult(); return Task.CompletedTask; };
        using var clients = new HttpFactory(handler);
        await using var provider = PipelineProvider(f, clients);
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, new(), f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = worker.RunBatchAsync(stop.Token, discover: false);
        try
        {
            await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await fastAudio.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.False(run.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref slowRequests));
            Assert.True(Volatile.Read(ref fastRequests) >= 1);
            // The free text lane refills while the first one is still held.
            for (var i = 0; i < 200 && Volatile.Read(ref fastRequests) < 2; i++) await Task.Delay(10, Ct);
            Assert.Equal(2, Volatile.Read(ref fastRequests));
        }
        finally { release.TrySetResult(); await stop.CancelAsync(); try { await run; } catch (OperationCanceledException) { } }
        await using var reopened = f.Open();
        Assert.Empty(await reopened.EnrichmentServiceLeases.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Pipeline_MissingTextConfigurationDefersWithoutRequestsOrLosingWork()
    {
        await using var f = await Fixture.Create(shared: true);
        await f.Publish(new() { ["Enrichment:Text:ApiKey"] = "" });
        var target = await f.Ensure(Key());
        using var handler = new BatchHandler(false); using var clients = new HttpFactory(handler);
        await using var provider = PipelineProvider(f, clients);
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, new(), f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        await worker.RunBatchAsync(Ct, discover: false);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, f.Http.SpeechCalls);
        await using var reopened = f.Open();
        var task = (await new EnrichmentTaskRepository(reopened, f.Clock).GetAsync(target.Id, Ct))!;
        Assert.Equal(EnrichmentState.Pending, Round(task).State);
        Assert.Equal(0, Round(task).ConsumedFailures);
        await f.Publish(new() { ["Enrichment:Text:ApiKey"] = "synthetic-text" });
        await worker.RunBatchAsync(Ct, discover: false);
        task = (await new EnrichmentTaskRepository(reopened, f.Clock).GetAsync(target.Id, Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(task).State);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, f.Http.SpeechCalls);
    }

    [Fact]
    public async Task TextConcurrency_UsesSharedCooldown_AndPolicyChangeDoesNotInvalidatePlans()
    {
        await using var f = await Fixture.Create();
        var version = f.Runtime.Current.TextVersion;
        await f.Publish(new() { ["Enrichment:TextConcurrency"] = "2" });
        Assert.Equal(version, f.Runtime.Current.TextVersion);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var gate = new EnrichmentServiceGate(scheduler, f.Runtime, f.Clock);
        var a = await gate.EnterAsync("text", false, Ct); var b = await gate.EnterAsync("text", false, Ct);
        Assert.NotNull(a.LeaseId); Assert.NotNull(b.LeaseId);
        Assert.Null((await gate.EnterAsync("text", false, Ct)).LeaseId);
        await gate.FinishAsync(a, "Throttled", "batch.throttled", true, TimeSpan.FromSeconds(30), Ct);
        await gate.FinishAsync(b, "Ready", null, true, null, Ct);
        Assert.Null((await gate.EnterAsync("text", false, Ct)).LeaseId);
        Assert.True((await scheduler.ServiceAsync(a.ServiceId, Ct))!.NextAttemptAtUnixMs >= f.Clock.Now + 30000);
    }

    private static ServiceProvider PipelineProvider(Fixture f, IHttpClientFactory clients)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => f.Open());
        services.AddScoped(p => new EnrichmentTaskRepository(p.GetRequiredService<VocabularyDbContext>(), f.Clock));
        services.AddScoped<IEnrichmentTaskRepository>(p => p.GetRequiredService<EnrichmentTaskRepository>());
        services.AddScoped<IEnrichmentSchedulerRepository>(p => new EnrichmentSchedulerRepository(p.GetRequiredService<VocabularyDbContext>(), p.GetRequiredService<EnrichmentTaskRepository>()));
        services.AddScoped<IAiRequestBatchRepository>(p => new AiRequestBatchRepository(p.GetRequiredService<VocabularyDbContext>(), p.GetRequiredService<EnrichmentTaskRepository>(),
            new MeaningSubmissionAutoReviewRepository(p.GetRequiredService<VocabularyDbContext>(), f.Clock), p.GetRequiredService<IEnrichmentSchedulerRepository>()));
        services.AddScoped(p => new EnrichmentServiceGate(p.GetRequiredService<IEnrichmentSchedulerRepository>(), f.Runtime, f.Clock));
        services.AddScoped(p => f.ExecutorFor(p.GetRequiredService<VocabularyDbContext>(), p.GetRequiredService<EnrichmentTaskRepository>()));
        services.AddScoped(p => BatchExecutor(f, p.GetRequiredService<VocabularyDbContext>(), p.GetRequiredService<EnrichmentTaskRepository>(),
            (EnrichmentSchedulerRepository)p.GetRequiredService<IEnrichmentSchedulerRepository>(), p.GetRequiredService<IAiRequestBatchRepository>(), clients));
        services.AddSingleton<IPronunciationPreparationClient>(new OpenAiCompatiblePronunciationPreparationClient(clients, f.Runtime, f.Clock));
        services.AddSingleton<IPronunciationSpeechClient>(new PronunciationSpeechClient(clients, f.Runtime, f.Clock));
        services.AddSingleton<IAudioUploadOperationFactory>(f.Operations);
        return services.BuildServiceProvider();
    }
}
