using System.Data.Common;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host;
using Lexarbor.Host.Integration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task BatchCommitBusy_RetriesOnlyPersistence_AndReleasesServiceOnExhaustion(int failures)
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(EnrichmentStep.Fields));
        var fault = new BusyBatchCommit();
        await using var db = f.Open(fault);
        var tasks = new EnrichmentTaskRepository(db, f.Clock);
        var scheduler = new EnrichmentSchedulerRepository(db, tasks);
        var batches = new AiRequestBatchRepository(db, tasks, new MeaningSubmissionAutoReviewRepository(db, f.Clock), scheduler);
        using var clients = new HttpFactory(f.Http);
        f.Http.BeforeTextResponse = () => { fault.Remaining = failures; return Task.CompletedTask; };
        var executor = BatchExecutor(f, db, tasks, scheduler, batches, clients);
        if (failures < 3) await executor.PrepareAsync([task.Id], Ct);
        else await Assert.ThrowsAsync<StorageBusyException>(() => executor.PrepareAsync([task.Id], Ct));
        Assert.Equal(1, f.Http.TextCalls);
        var prepare = await tasks.GetAsync((await f.Ensure(Key(EnrichmentStep.Prepare))).Id, Ct);
        Assert.Equal(1, Round(prepare!).RequestCount);
        Assert.Equal(0, Round(prepare!).ConsumedFailures);
        Assert.Equal(failures < 3, Round(prepare!).Plan != null);
        Assert.Equal(failures < 3 ? EnrichmentCallResult.Succeeded : EnrichmentCallResult.Started, Assert.Single(prepare!.Calls).Result);
        Assert.Equal(failures < 3 ? 0 : 1, (await batches.StatisticsAsync(Ct)).Unknown);
        var status = (await scheduler.ServiceAsync(EnrichmentServiceGate.Identity("text", f.Runtime.Current), Ct))!;
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.False(status.Paused);
        if (failures < 4) Assert.Null(status.LeaseId);
        else Assert.Equal(f.Clock.Now + 90_000, status.LeaseExpiresAtUnixMs);
    }

    [Fact]
    public async Task BatchBeginFailure_RollsBackCalls_AndReleasesServiceBeforeAnyHttpRequest()
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(EnrichmentStep.Fields));
        await using var db = f.Open(new FailBatchBegin());
        var tasks = new EnrichmentTaskRepository(db, f.Clock);
        var scheduler = new EnrichmentSchedulerRepository(db, tasks);
        var batches = new AiRequestBatchRepository(db, tasks, new MeaningSubmissionAutoReviewRepository(db, f.Clock), scheduler);
        using var clients = new HttpFactory(f.Http);
        var executor = BatchExecutor(f, db, tasks, scheduler, batches, clients);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.PrepareAsync([task.Id], Ct));
        Assert.Equal(0, f.Http.TextCalls);
        Assert.Empty(await db.EnrichmentCalls.AsNoTracking().ToArrayAsync(Ct));
        Assert.Empty(await db.AiRequestBatches.AsNoTracking().ToArrayAsync(Ct));
        Assert.Null((await scheduler.ServiceAsync(EnrichmentServiceGate.Identity("text", f.Runtime.Current), Ct))!.LeaseId);
    }

    [Fact]
    public async Task BatchCancellation_ReleasesServiceWithUncancelledCleanupToken()
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(EnrichmentStep.Fields));
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        using var clients = new HttpFactory(f.Http);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Http.BeforeTextResponse = () => { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var executor = BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.PrepareAsync([task.Id], stop.Token));
        Assert.Equal(1, f.Http.TextCalls);
        var status = (await scheduler.ServiceAsync(EnrichmentServiceGate.Identity("text", f.Runtime.Current), Ct))!;
        Assert.Null(status.LeaseId);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task SuccessfulSingleBatch_RestoresPersistedLimit_AcrossRepositoryRestart()
    {
        await using var f = await Fixture.Create();
        f.Db.AiRequestBatches.Add(new()
        {
            Id = "previous",
            Kind = "Prepare",
            ConfigurationVersion = f.Runtime.Current.TextVersion!,
            Outcome = "Invalid",
            NextSize = 1,
            StartedAtUnixMs = 1,
            FinishedAtUnixMs = 2
        });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var task = await f.Ensure(Key(EnrichmentStep.Fields));
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        using var clients = new HttpFactory(f.Http);
        await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).PrepareAsync([task.Id], Ct);
        await using var reopened = f.Open();
        var tasks = new EnrichmentTaskRepository(reopened, f.Clock);
        var restarted = new AiRequestBatchRepository(reopened, tasks, new MeaningSubmissionAutoReviewRepository(reopened, f.Clock),
            new EnrichmentSchedulerRepository(reopened, tasks));
        Assert.Equal(2, await restarted.LimitAsync("Prepare", f.Runtime.Current.TextVersion!, 6, Ct));
        Assert.Equal(1, f.Http.TextCalls);
        Assert.Equal(1, (await restarted.StatisticsAsync(Ct)).Completed);
    }

    [Theory]
    [InlineData("text", 120, 15, 180_000)]
    [InlineData("tts", 120, 15, 75_000)]
    [InlineData("text", 300, 15, 360_000)]
    public async Task ServiceLease_CoversItsProviderTimeoutAndCommitMargin(string service, int textTimeout, int ttsTimeout, long duration)
    {
        await using var f = await Fixture.Create();
        await f.Publish(new() { ["Enrichment:Text:TimeoutSeconds"] = textTimeout.ToString(), ["Enrichment:Tts:TimeoutSeconds"] = ttsTimeout.ToString() });
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var gate = new EnrichmentServiceGate(scheduler, f.Runtime, f.Clock);
        var first = await gate.EnterAsync(service, false, Ct);
        Assert.NotNull(first.LeaseId);
        Assert.Equal(f.Clock.Now + duration, (await scheduler.ServiceAsync(first.ServiceId, Ct))!.LeaseExpiresAtUnixMs);
        f.Clock.Now += duration - 1;
        Assert.Null((await gate.EnterAsync(service, false, Ct)).LeaseId);
        f.Clock.Now++;
        var replacement = await gate.EnterAsync(service, false, Ct);
        Assert.NotNull(replacement.LeaseId);
        await gate.FinishAsync(first, "Superseded", null, false, null, Ct);
        Assert.Equal(replacement.LeaseId, (await scheduler.ServiceAsync(first.ServiceId, Ct))!.LeaseId);
    }

    private static EnrichmentTextBatchExecutor BatchExecutor(Fixture f, Lexarbor.Database.VocabularyDbContext db,
        EnrichmentTaskRepository tasks, EnrichmentSchedulerRepository scheduler, IAiRequestBatchRepository batches, IHttpClientFactory clients) =>
        new(tasks, new EnrichmentExecutionRepository(db, tasks, f.Clock), new VocabularyExpansionRepository(db), new VocabularyBookRepository(db),
            batches, new AiTextBatchClient(clients, f.Clock), new(scheduler, f.Runtime, f.Clock), f.Runtime, new() { BookId = "book" }, f.Operations, f.Clock);

    private sealed class BusyBatchCommit : DbTransactionInterceptor
    {
        public int Remaining;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Remaining-- > 0) throw new SqliteException("Synthetic busy commit.", 5);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailBatchBegin : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"ai_request_batch\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic batch begin failure.");
            return ValueTask.FromResult(result);
        }
    }
}
