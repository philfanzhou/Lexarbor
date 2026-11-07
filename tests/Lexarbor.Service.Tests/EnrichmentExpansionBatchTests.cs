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
    public async Task ExpansionBatch_GrowsFromTwoToThree_AndRetainsFailureBackoffAcrossRestart(string failure)
    {
        await using var f = await Fixture.Create();
        var ids = await ExpansionSources(f, 8);
        using var handler = new ExpansionBatchHandler
        {
            Failure = call => { f.Clock.Now += 100; return call == 2 ? failure : null; }
        };
        using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).DispatchAsync(ids[..5], Ct);
        Assert.Equal(new[] { 2, 3 }, handler.Sizes);
        Assert.Equal(1, await batches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 2, 3, Ct));
        Assert.Equal(2, (await batches.StatisticsAsync(Ct)).Completed);
        foreach (var id in ids.Take(2)) Assert.NotNull(Round((await f.Tasks.GetAsync(id, Ct))!).Plan);
        f.Clock.Now = (await f.Tasks.GetAsync(ids[2], Ct))!.Rounds.Last().NextAttemptAtUnixMs!.Value + 1;

        await using var restart = f.Open();
        var tasks = new EnrichmentTaskRepository(restart, f.Clock);
        var restartedScheduler = new EnrichmentSchedulerRepository(restart, tasks);
        var restartedBatches = new AiRequestBatchRepository(restart, tasks,
            new MeaningSubmissionAutoReviewRepository(restart, f.Clock), restartedScheduler);
        Assert.Equal(1, await restartedBatches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 2, 3, Ct));
        var executor = BatchExecutor(f, restart, tasks, restartedScheduler, restartedBatches, clients);
        await executor.DispatchAsync(ids[..5], Ct); // The two saved results must not be requested again.
        await executor.DispatchAsync(ids[5..], Ct);
        Assert.Equal(new[] { 2, 3, 1, 2, 3 }, handler.Sizes);
        Assert.Equal(3, await restartedBatches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 2, 3, Ct));
        foreach (var id in ids.Take(2)) Assert.Single((await tasks.GetAsync(id, Ct))!.Calls);
        foreach (var id in ids.Skip(2).Take(3)) Assert.Equal(2, (await tasks.GetAsync(id, Ct))!.Calls.Count);
        foreach (var id in ids)
        {
            var task = (await tasks.GetAsync(id, Ct))!;
            Assert.Equal(EnrichmentState.Succeeded,
                Round((await Expansion(f, restart, clients, "book").ExecuteAsync(task, "publish", Ct))!).State);
        }
        Assert.Equal(5, handler.Calls);
        Assert.Empty(await restart.EnrichmentServiceLeases.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task ExpansionBatch_PartialResponsePreservesValidMember_AndRetriesOnlyMissingMember()
    {
        await using var f = await Fixture.Create();
        var ids = await ExpansionSources(f, 2);
        using var handler = new ExpansionBatchHandler { OmitLast = call => call == 1 };
        using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).DispatchAsync(ids, Ct);
        Assert.NotNull(Round((await f.Tasks.GetAsync(ids[0], Ct))!).Plan);
        Assert.Null(Round((await f.Tasks.GetAsync(ids[1], Ct))!).Plan);
        Assert.Equal(1, (await batches.StatisticsAsync(Ct)).Completed);
        Assert.Equal(1, await batches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 2, 3, Ct));
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
        Assert.Equal(2, await rb.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 2, 3, Ct));
    }

    [Fact]
    public async Task ExpansionBatch_LegacyLimitIsPreserved_WhileNewConfigurationStartsAtTwo()
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
        Assert.Equal(1, await batches.LimitAsync("Expand", f.Runtime.Current.TextVersion!, 2, 3, Ct));
        Assert.Equal(2, await batches.LimitAsync("Expand", "new-configuration", 2, 3, Ct));
        Assert.Equal(6, await batches.LimitAsync("Prepare", f.Runtime.Current.TextVersion!, 6, 6, Ct));
        Assert.Equal(6, await batches.LimitAsync("Review", f.Runtime.Current.TextVersion!, 6, 6, Ct));
    }

    [Fact]
    public async Task Worker_UsesTwoExpansionBatches_ThenGivesNewPreparationTheNextFreeSlot()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        await f.Publish(new() { ["Enrichment:TextConcurrency"] = "2", ["Enrichment:Tts:ApiKey"] = "" });
        await ExpansionSources(f, 8);
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
        public Func<int, string?>? Failure;
        public Func<int, bool>? OmitLast;
        public Func<IReadOnlyList<string>, CancellationToken, Task>? BeforeSend;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref Calls);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
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
