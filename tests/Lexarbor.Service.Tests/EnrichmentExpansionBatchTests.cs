using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Theory]
    [InlineData("oversized")]
    [InlineData("timeout")]
    public async Task ExpansionBatch_GrowsOnlyAfterThreeFastSuccesses_AndPinsFailuresAcrossRestart(string failure)
    {
        await using var f = await Fixture.Create();
        var ids = await ExpansionSources(f, 10);
        using var handler = new ExpansionBatchHandler
        {
            Failure = call => { f.Clock.Now += 100; return call == 4 ? failure : null; }
        };
        using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).DispatchAsync(ids[..5], Ct);
        Assert.Equal(new[] { 1, 1, 1, 2 }, handler.Sizes);
        Assert.Equal(1, await batches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
        Assert.Equal(3, (await batches.StatisticsAsync(Ct)).Completed);
        foreach (var id in ids.Take(3)) Assert.NotNull(Round((await f.Tasks.GetAsync(id, Ct))!).Plan);
        f.Clock.Now = (await f.Tasks.GetAsync(ids[3], Ct))!.Rounds.Last().NextAttemptAtUnixMs!.Value + 1;

        await using var restart = f.Open();
        var tasks = new EnrichmentTaskRepository(restart, f.Clock);
        var restartedScheduler = new EnrichmentSchedulerRepository(restart, tasks);
        var restartedBatches = new AiRequestBatchRepository(restart, tasks,
            new MeaningSubmissionAutoReviewRepository(restart, f.Clock), restartedScheduler);
        Assert.Equal(1, await restartedBatches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
        var executor = BatchExecutor(f, restart, tasks, restartedScheduler, restartedBatches, clients);
        await executor.DispatchAsync(ids[..5], Ct); // The three saved results must not be requested again.
        await executor.DispatchAsync(ids[5..], Ct);
        Assert.Equal(new[] { 1, 1, 1, 2, 1, 1, 1, 2, 2 }, handler.Sizes);
        Assert.Equal(2, await restartedBatches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
        foreach (var id in ids.Take(3)) Assert.Single((await tasks.GetAsync(id, Ct))!.Calls);
        foreach (var id in ids.Skip(3).Take(2)) Assert.Equal(2, (await tasks.GetAsync(id, Ct))!.Calls.Count);
        foreach (var id in ids)
        {
            var task = (await tasks.GetAsync(id, Ct))!;
            Assert.Equal(EnrichmentState.Succeeded,
                Round((await Expansion(f, restart, clients, "book").ExecuteAsync(task, "publish", Ct))!).State);
        }
        Assert.Equal(9, handler.Calls);
        Assert.Empty(await restart.EnrichmentServiceLeases.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task ExpansionBatch_PartialResponsePreservesValidMember_AndRetriesOnlyMissingMember()
    {
        await using var f = await Fixture.Create();
        var ids = await ExpansionSources(f, 2);
        await SetExpansionSizeTwo(f);
        using var handler = new ExpansionBatchHandler { OmitLast = call => call == 1 };
        using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).DispatchAsync(ids, Ct);
        Assert.NotNull(Round((await f.Tasks.GetAsync(ids[0], Ct))!).Plan);
        Assert.Null(Round((await f.Tasks.GetAsync(ids[1], Ct))!).Plan);
        Assert.Equal(1, (await batches.StatisticsAsync(Ct)).Completed);
        Assert.Equal(1, await batches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
        f.Clock.Now = Round((await f.Tasks.GetAsync(ids[1], Ct))!).NextAttemptAtUnixMs!.Value + 1;
        await using var restart = f.Open();
        var tasks = new EnrichmentTaskRepository(restart, f.Clock);
        var rs = new EnrichmentSchedulerRepository(restart, tasks);
        var rb = new AiRequestBatchRepository(restart, tasks, new MeaningSubmissionAutoReviewRepository(restart, f.Clock), rs);
        await BatchExecutor(f, restart, tasks, rs, rb, clients).DispatchAsync(ids, Ct);
        Assert.Equal(new[] { 2, 1 }, handler.Sizes);
        Assert.Single((await tasks.GetAsync(ids[0], Ct))!.Calls);
        Assert.Equal(2, (await tasks.GetAsync(ids[1], Ct))!.Calls.Count);
        Assert.Equal(2, (await rb.StatisticsAsync(Ct)).Completed);
        Assert.Equal(1, await rb.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
    }

    [Fact]
    public async Task ExpansionBatch_LegacyAndNewConfigurationStartConservatively()
    {
        await using var f = await Fixture.Create();
        f.Db.AiRequestBatches.Add(new()
        {
            Id = "legacy-expansion",
            Kind = "Expand",
            ConfigurationVersion = f.Runtime.Current.TextVersion!,
            Outcome = "Ready",
            NextSize = 1,
            StartedAtUnixMs = 1,
            FinishedAtUnixMs = 2
        });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        await using var restart = f.Open();
        var tasks = new EnrichmentTaskRepository(restart, f.Clock);
        var batches = new AiRequestBatchRepository(restart, tasks, new MeaningSubmissionAutoReviewRepository(restart, f.Clock),
            new EnrichmentSchedulerRepository(restart, tasks));
        Assert.Equal(1, await batches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
        Assert.Equal(1, await batches.LimitAsync("Expand", "new-configuration", 2, 3, Ct));
        Assert.Equal(6, await batches.LimitAsync("Prepare", f.Runtime.Current.TextVersion!, 6, 6, Ct));
        Assert.Equal(6, await batches.LimitAsync("Review", f.Runtime.Current.TextVersion!, 6, 6, Ct));
    }

    [Fact]
    public async Task Worker_UsesTwoExpansionBatches_ThenGivesNewPreparationTheNextFreeSlot()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        await f.Publish(new() { ["Enrichment:TextConcurrency"] = "2", ["Enrichment:Tts:ApiKey"] = "" });
        await ExpansionSources(f, 8);
        await SetExpansionSizeTwo(f);
        // A second sense of one source must not run beside that source's first sense.
        f.Db.VocabularyMeanings.Add(new() { Id = "other-sense", BookId = "book", VocabularyId = "source0", Meaning = "another sense", PartOfSpeech = "v." });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        await f.Ensure(new("source0", "other-sense", null, EnrichmentStep.Expand));
        // Put disabled speech work into its normal deferred state before testing text-only capacity.
        foreach (var key in await new EnrichmentSchedulerRepository(f.Db, f.Tasks).DiscoverAsync(100, [], Ct))
            if (key.Step == EnrichmentStep.Audio) await f.Run(key);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeWords = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var expansionCalls = 0;
        using var expansionHandler = new ExpansionBatchHandler
        {
            BeforeSend = async (words, token) =>
            {
                Assert.Equal(2, words.Count);
                foreach (var word in words) Assert.True(activeWords.TryAdd(word, 0), "A source word entered two concurrent requests.");
                var call = Interlocked.Increment(ref expansionCalls);
                try
                {
                    if (call == 1) { firstEntered.TrySetResult(); await firstRelease.Task.WaitAsync(token); }
                    else if (call == 2) { bothEntered.TrySetResult(); await secondRelease.Task.WaitAsync(token); }
                    else Assert.Fail("Another expansion displaced queued preparation.");
                }
                finally { foreach (var word in words) activeWords.TryRemove(word, out _); }
            }
        };
        using var preparationHandler = new BatchHandler(false)
        {
            BeforeSend = async (_, token) => { preparationEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        };
        using var router = new ExpansionPreparationHandler(expansionHandler, preparationHandler);
        using var clients = new HttpFactory(router);
        await using var provider = PipelineProvider(f, clients, publishExpansion: true);
        var notifications = new EnrichmentNotifications();
        using var worker = new EnrichmentWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Runtime, notifications, f.Clock,
            NullLogger<EnrichmentWorker>.Instance);
        await worker.StartAsync(Ct);
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(4, activeWords.Count);
            Assert.Equal(2, await f.Db.EnrichmentServiceLeases.CountAsync(Ct));
            f.Db.Vocabularies.Add(new() { Id = "new-preparation", Word = "new-preparation" });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            await f.Ensure(new("new-preparation", null, AudioLocale.EnGb, EnrichmentStep.Prepare));
            notifications.Notify(["new-preparation"]);
            firstRelease.TrySetResult();
            await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(2, Volatile.Read(ref expansionCalls));
            Assert.False(secondRelease.Task.IsCompleted);
        }
        finally { await worker.StopAsync(Ct); }
        Assert.Empty(await f.Db.EnrichmentServiceLeases.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task TextLane_PrefersPreparationAndFieldsOverDisabledAudioForTheSameWord()
    {
        await using var f = await Fixture.Create();
        await f.Publish(new() { ["Enrichment:Tts:ApiKey"] = "" });
        var fields = await f.Ensure(Key(EnrichmentStep.Fields));
        var audio = await f.Ensure(Key());
        var prepare = await f.Ensure(Key(EnrichmentStep.Prepare));
        // Even an older dependent audio target cannot hide ready text work.
        await f.Db.EnrichmentRounds.Where(r => r.TargetId == audio.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.CreatedAtUnixMs, 0).SetProperty(r => r.UpdatedAtUnixMs, 0), Ct);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        Assert.Equal(prepare.Id, Assert.Single((await scheduler.PlanAsync(10, f.Clock.Now, [], Ct)), i => i.Lane == "Text").Id);
        await f.Db.EnrichmentTargets.Where(t => t.Id == prepare.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Deleted, true), Ct);
        Assert.Equal(fields.Id, Assert.Single((await scheduler.PlanAsync(10, f.Clock.Now, [], Ct)), i => i.Lane == "Text").Id);
    }

    [Theory]
    [InlineData(59_999, 2)]
    [InlineData(60_000, 1)]
    public async Task ExpansionGrowth_RequiresThreeConsecutiveResponsesBelowOneMinute(long middleDuration, int expected)
    {
        await using var f = await Fixture.Create();
        var ids = await ExpansionSources(f, 3);
        using var handler = new ExpansionBatchHandler { Failure = call => { f.Clock.Now += call == 2 ? middleDuration : 100; return null; } };
        using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).DispatchAsync(ids, Ct);
        Assert.Equal(new[] { 1, 1, 1 }, handler.Sizes);
        Assert.Equal(expected, await batches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
    }

    [Fact]
    public async Task ExpansionLateSuccess_CannotUndoConcurrentFailureBackoff()
    {
        await using var f = await Fixture.Create();
        await f.Publish(new() { ["Enrichment:TextConcurrency"] = "2" });
        var ids = await ExpansionSources(f, 2);
        await SetExpansionSizeTwo(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ExpansionBatchHandler
        {
            BeforeSend = async (words, token) =>
            { if (words.Contains("source0")) { entered.TrySetResult(); await release.Task.WaitAsync(token); } },
            Failure = call => { f.Clock.Now += 100; return call == 2 ? "timeout" : null; }
        };
        using var clients = new HttpFactory(handler);
        await using var firstDb = f.Open(); await using var secondDb = f.Open();
        var firstTasks = new EnrichmentTaskRepository(firstDb, f.Clock); var secondTasks = new EnrichmentTaskRepository(secondDb, f.Clock);
        var firstScheduler = new EnrichmentSchedulerRepository(firstDb, firstTasks); var secondScheduler = new EnrichmentSchedulerRepository(secondDb, secondTasks);
        var firstBatches = new AiRequestBatchRepository(firstDb, firstTasks, new MeaningSubmissionAutoReviewRepository(firstDb, f.Clock), firstScheduler);
        var secondBatches = new AiRequestBatchRepository(secondDb, secondTasks, new MeaningSubmissionAutoReviewRepository(secondDb, f.Clock), secondScheduler);
        var first = BatchExecutor(f, firstDb, firstTasks, firstScheduler, firstBatches, clients).DispatchAsync([ids[0]], Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await BatchExecutor(f, secondDb, secondTasks, secondScheduler, secondBatches, clients).DispatchAsync([ids[1]], Ct);
            Assert.Equal(1, await secondBatches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 1, 2, Ct));
        }
        finally { release.TrySetResult(); await first; }
        var policy = await f.Db.AiRequestBatchPolicies.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(1, policy.Size); Assert.Equal(0, policy.FastSuccesses);
        Assert.True(Round((await f.Tasks.GetAsync(ids[1], Ct))!).ForceSingle);
        Assert.NotNull(Round((await f.Tasks.GetAsync(ids[0], Ct))!).Plan);
    }

    private static async Task SetExpansionSizeTwo(Fixture f)
    {
        f.Db.AiRequestBatchPolicies.Add(new() { Kind = "Expand", ConfigurationVersion = f.Runtime.Current.TextVersion!, Size = 2, FastSuccesses = 3 });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
    }

    private static async Task<string[]> ExpansionSources(Fixture f, int count)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var word = "source" + i; var meaning = "source-meaning" + i;
            f.Db.Vocabularies.Add(new() { Id = word, Word = word, PhoneticUk = "sɔːs", PhoneticUs = "sɔːrs" });
            f.Db.VocabularyMeanings.Add(new() { Id = meaning, VocabularyId = word, BookId = "book", Meaning = "fixture sense", PartOfSpeech = "n." });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
            ids.Add((await f.Ensure(new(word, meaning, null, EnrichmentStep.Expand))).Id);
        }
        return ids.ToArray();
    }

    private sealed class ExpansionBatchHandler : HttpMessageHandler
    {
        public int Calls;
        public ConcurrentQueue<int> Sizes { get; } = new();
        public ConcurrentQueue<string> Bodies { get; } = new();
        public Func<int, string?>? Failure;
        public Func<int, bool>? OmitLast;
        public Func<IReadOnlyList<string>, CancellationToken, Task>? BeforeSend;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref Calls);
            var rawBody = await request.Content!.ReadAsStringAsync(token);
            Bodies.Enqueue(rawBody);
            var body = JsonNode.Parse(rawBody)!;
            var payload = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            var single = payload["items"] == null;
            var items = single ? new JsonArray(new JsonObject { ["id"] = "single", ["data"] = payload }) : payload["items"]!.AsArray();
            Sizes.Enqueue(items.Count);
            Assert.All(items, item => Assert.NotNull(item!["data"]!["relationTypes"]));
            if (BeforeSend != null) await BeforeSend(items.Select(i => i!["data"]!["word"]!.GetValue<string>()).ToArray(), token);
            switch (Failure?.Invoke(call))
            {
                case "timeout": throw new OperationCanceledException("Synthetic provider timeout.");
                case "oversized": return new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) };
            }
            var rows = new JsonArray();
            foreach (var item in items.Take(items.Count - (OmitLast?.Invoke(call) == true ? 1 : 0)).Reverse())
                rows.Add(new JsonObject { ["id"] = item!["id"]!.DeepClone(), ["result"] = new JsonObject { ["candidates"] = new JsonArray() } });
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new
                { content = single ? rows[0]!["result"]!.ToJsonString() : new JsonObject { ["results"] = rows }.ToJsonString() } } }
                })
            };
        }
    }

    private sealed class ExpansionPreparationHandler(HttpMessageHandler expansion, HttpMessageHandler preparation) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _expansion = new(expansion, false);
        private readonly HttpMessageInvoker _preparation = new(preparation, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var instructions = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            return await (instructions.Contains("direct derivatives", StringComparison.Ordinal) ? _expansion : _preparation).SendAsync(request, token);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { _expansion.Dispose(); _preparation.Dispose(); } base.Dispose(disposing); }
    }
}
