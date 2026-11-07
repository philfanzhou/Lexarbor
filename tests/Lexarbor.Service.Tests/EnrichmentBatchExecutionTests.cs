using System.Data.Common;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchedPreparation_OneRequestStagesIndependentPlans_ThenRestartPublishesWithoutCallingAgain(bool partial)
    {
        await using var f = await Fixture.Create();
        f.Db.Vocabularies.Add(new() { Id = "second", Word = "walk" });
        f.Db.VocabularyMeanings.Add(new() { Id = "meaning2", BookId = "book", VocabularyId = "second", Meaning = "move" });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var first = await f.Ensure(Key(EnrichmentStep.Fields));
        var second = await f.Ensure(new("second", null, AudioLocale.EnGb, EnrichmentStep.Fields));
        using var handler = new BatchHandler(partial); using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        var executor = new EnrichmentTextBatchExecutor(f.Tasks, new EnrichmentExecutionRepository(f.Db, f.Tasks, f.Clock),
            new VocabularyExpansionRepository(f.Db), new VocabularyBookRepository(f.Db), batches,
            new AiTextBatchClient(clients, f.Clock), new(scheduler, f.Runtime, f.Clock), f.Runtime, new() { BookId = "book" }, f.Operations, f.Clock);
        var selected = await executor.PrepareAsync([first.Id, second.Id], Ct);
        Assert.Equal(2, selected.Count); Assert.Equal(1, handler.Calls);
        var stats = await batches.StatisticsAsync(Ct);
        Assert.Equal(1, stats.Requests); Assert.Equal(2, stats.Participations); Assert.Equal(partial ? 1 : 2, stats.Completed);
        Assert.Equal(partial ? 3 : 6, await batches.LimitAsync("Prepare", f.Runtime.Current.TextVersion!, 6, Ct));
        await using var restarted = f.Open(); var tasks = new EnrichmentTaskRepository(restarted, f.Clock);
        await f.ExecutorFor(restarted, tasks).ExecuteAsync(first.Id, "restart", Ct);
        if (!partial) await f.ExecutorFor(restarted, tasks).ExecuteAsync(second.Id, "restart", Ct);
        Assert.Equal(0, f.Http.TextCalls);
        Assert.Equal("liːd", await restarted.Vocabularies.Where(w => w.Id == "word").Select(w => w.PhoneticUk).SingleAsync(Ct));
        Assert.Equal(partial ? null : "wɔːk", await restarted.Vocabularies.Where(w => w.Id == "second").Select(w => w.PhoneticUk).SingleAsync(Ct));
        var failed = await tasks.EnsureAsync(new("second", null, AudioLocale.EnGb, EnrichmentStep.Prepare),
            EnrichmentTargetExecutor.ConfigurationVersion(new("second", null, AudioLocale.EnGb, EnrichmentStep.Prepare), f.Runtime.Current), f.Clock.Now, Ct);
        Assert.Equal(partial ? 1 : 0, Round(failed!).ConsumedFailures);
    }
    [Fact]
    public async Task BatchedPreparationSerializesTargetsOfTheSameWord()
    {
        await using var f = await Fixture.Create();
        var first = await f.Ensure(Key(EnrichmentStep.Fields)); var second = await f.Ensure(Key(EnrichmentStep.Fields, AudioLocale.EnUs));
        using var clients = new HttpFactory(f.Http);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        var executor = new EnrichmentTextBatchExecutor(f.Tasks, new EnrichmentExecutionRepository(f.Db, f.Tasks, f.Clock),
            new VocabularyExpansionRepository(f.Db), new VocabularyBookRepository(f.Db), batches,
            new AiTextBatchClient(clients, f.Clock), new(scheduler, f.Runtime, f.Clock), f.Runtime, new() { BookId = "book" }, f.Operations, f.Clock);
        Assert.Single(await executor.PrepareAsync([first.Id, second.Id], Ct));
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, (await batches.StatisticsAsync(Ct)).Participations);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BatchPreparationHonorsExistingAudioOnlyConfigurationAndStorageGates(bool audio, bool storage)
    {
        await using var f = await Fixture.Create();
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(s => s.SetProperty(m => m.PhoneticUk, "liːd"), Ct);
        await f.Publish(new() { ["Enrichment:Tts:ApiKey"] = audio ? "synthetic-speech" : "" }); f.Operations.Enabled = storage;
        var task = await f.Ensure(Key(EnrichmentStep.Prepare, meaning: "meaning"));
        using var clients = new HttpFactory(f.Http);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        var executor = new EnrichmentTextBatchExecutor(f.Tasks, new EnrichmentExecutionRepository(f.Db, f.Tasks, f.Clock),
            new VocabularyExpansionRepository(f.Db), new VocabularyBookRepository(f.Db), batches,
            new AiTextBatchClient(clients, f.Clock), new(scheduler, f.Runtime, f.Clock), f.Runtime, new() { BookId = "book" }, f.Operations, f.Clock);
        Assert.Single(await executor.PrepareAsync([task.Id], Ct));
        Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, (await batches.StatisticsAsync(Ct)).Requests);
        var result = await f.Executor.ExecuteAsync(task.Id, "worker", Ct);
        Assert.Equal(storage ? EnrichmentWaitReason.ConfigurationMissing : EnrichmentWaitReason.StorageDisabled, Round(result!).WaitReason);
    }

    [Fact]
    public async Task BatchResultTransactionRollsBackEveryMemberAndReplaysWithoutDoubleCounting()
    {
        await using var f = await Fixture.Create();
        f.Db.Vocabularies.Add(new() { Id = "second", Word = "walk" }); await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var first = await f.Ensure(Key(EnrichmentStep.Prepare));
        var second = await f.Ensure(new("second", null, AudioLocale.EnGb, EnrichmentStep.Prepare));
        var members = new List<AiBatchMember>();
        foreach (var task in new[] { first, second }) members.Add(new(Guid.NewGuid().ToString(),
            (await f.Tasks.ClaimAsync(task.Id, "batch", f.Clock.Now, f.Clock.Now + 10000, Ct))!));
        var fault = new FailSecondBatchPlanWrite();
        await using var writer = f.Open(fault); var tasks = new EnrichmentTaskRepository(writer, f.Clock);
        var scheduler = new EnrichmentSchedulerRepository(writer, tasks);
        var repo = new AiRequestBatchRepository(writer, tasks, new MeaningSubmissionAutoReviewRepository(writer, f.Clock), scheduler);
        var receipt = (await repo.BeginAsync("Prepare", "v", members, f.Clock.Now, Ct))!;
        var results = members.Select(m => new AiBatchResult(m.Id, new("fixture", null, null, []))).ToArray();
        fault.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.CompleteAsync(receipt, results, "Ready", null, 6, f.Clock.Now, null, Ct));
        Assert.Equal(2, fault.Writes);
        Assert.All(await writer.EnrichmentRounds.AsNoTracking().ToArrayAsync(Ct), r => Assert.Null(r.PlanJson));
        Assert.All(await writer.EnrichmentCalls.AsNoTracking().ToArrayAsync(Ct), r => Assert.Equal(EnrichmentCallResult.Started, r.Result));
        Assert.Null((await writer.AiRequestBatches.SingleAsync(Ct)).FinishedAtUnixMs); writer.ChangeTracker.Clear();
        fault.Enabled = false;
        await repo.CompleteAsync(receipt, results, "Ready", null, 6, f.Clock.Now, null, Ct);
        await repo.CompleteAsync(receipt, results, "Ready", null, 6, f.Clock.Now, null, Ct);
        Assert.All(await writer.EnrichmentRounds.AsNoTracking().ToArrayAsync(Ct), r => { Assert.NotNull(r.PlanJson); Assert.Equal(1, r.RequestCount); Assert.Equal(0, r.ConsumedFailures); });
        Assert.Equal(new AiBatchStatistics(1, 2, 2, 0), await repo.StatisticsAsync(Ct));
    }

    [Fact]
    public async Task BatchServiceFailureCountsOncePerRequestWhileEachMemberKeepsItsOwnBudget()
    {
        await using var f = await Fixture.Create();
        f.Db.Vocabularies.Add(new() { Id = "second", Word = "walk" }); await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var first = await f.Ensure(Key(EnrichmentStep.Prepare));
        var second = await f.Ensure(new("second", null, AudioLocale.EnGb, EnrichmentStep.Prepare));
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var repo = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        var admission = await scheduler.EnterServiceAsync("text", "v", false, f.Clock.Now, f.Clock.Now + 10000, Ct);
        var members = new List<AiBatchMember>();
        foreach (var task in new[] { first, second }) members.Add(new(Guid.NewGuid().ToString(),
            (await f.Tasks.ClaimAsync(task.Id, "batch", f.Clock.Now, f.Clock.Now + 10000, Ct))!));
        var receipt = (await repo.BeginAsync("Prepare", "v", members, f.Clock.Now, Ct))!;
        await repo.CompleteAsync(receipt, [], "ServiceFailure", "batch.service_unavailable", 6, f.Clock.Now, f.Clock.Now + 1000, Ct,
            admission, new(5, 60, 600, 8, 120));
        Assert.Equal(1, (await scheduler.ServiceAsync("text", Ct))!.ConsecutiveFailures);
        foreach (var task in new[] { first, second }) Assert.Equal(1, Round((await f.Tasks.GetAsync(task.Id, Ct))!).ConsumedFailures);
        Assert.Equal(2, (await repo.StatisticsAsync(Ct)).Participations);
    }

    [Fact]
    public async Task ExpansionKeepsSingleItemRequestsAndPublishesToTheExistingSupplement()
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        f.Db.Vocabularies.Add(new() { Id = "second", Word = "walk" });
        f.Db.VocabularyMeanings.Add(new() { Id = "meaning2", BookId = "book", VocabularyId = "second", Meaning = "move", PartOfSpeech = "v." });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var first = await f.Ensure(ExpansionKey);
        var second = await f.Ensure(new("second", "meaning2", null, EnrichmentStep.Expand));
        using var handler = new BatchHandler(false); using var clients = new HttpFactory(handler);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        var executor = new EnrichmentTextBatchExecutor(f.Tasks, new EnrichmentExecutionRepository(f.Db, f.Tasks, f.Clock),
            new VocabularyExpansionRepository(f.Db), new VocabularyBookRepository(f.Db), batches, new AiTextBatchClient(clients, f.Clock),
            new(scheduler, f.Runtime, f.Clock), f.Runtime, new() { BookId = "supplement" }, f.Operations, f.Clock);
        await executor.PrepareAsync([first.Id, second.Id], Ct);
        Assert.Equal(2, handler.Calls); Assert.Equal(2, (await batches.StatisticsAsync(Ct)).Completed);
        await using var reopened = f.Open();
        foreach (var task in new[] { first, second })
            Assert.Equal(EnrichmentState.Succeeded, Round((await Expansion(f, reopened, clients).ExecuteAsync(task, "restart", Ct))!).State);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, await reopened.VocabularyMeanings.CountAsync(m => m.BookId == "supplement", Ct));
        Assert.Equal(2, await reopened.EnrichmentGeneratedWords.CountAsync(Ct));
    }

    private sealed class FailSecondBatchPlanWrite : DbCommandInterceptor
    {
        public bool Enabled; public int Writes;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("UPDATE \"enrichment_round\"", StringComparison.Ordinal)
                && command.CommandText.Contains("plan_json", StringComparison.Ordinal) && ++Writes == 2)
                throw new InvalidOperationException("Synthetic failure after the first batch member was staged.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BatchHandler(bool partial) : HttpMessageHandler
    {
        public int Calls;
        public Func<string, CancellationToken, Task>? BeforeSend;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var request = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            var payload = JsonNode.Parse(request["messages"]![1]!["content"]!.GetValue<string>())!;
            var single = payload["items"] == null;
            var input = single ? new JsonArray(new JsonObject { ["id"] = "single", ["data"] = payload }) : payload["items"]!.AsArray();
            if (BeforeSend != null) await BeforeSend(input[0]!["data"]!["word"]!.GetValue<string>(), token);
            var rows = new JsonArray();
            foreach (var item in input.Reverse())
            {
                var data = item!["data"]!; var word = data["word"]!.GetValue<string>();
                if (data["relationTypes"] != null)
                {
                    rows.Add(new JsonObject
                    {
                        ["id"] = item["id"]!.DeepClone(),
                        ["result"] = JsonSerializer.SerializeToNode(new
                        { candidates = new[] { Candidate(word == "lead" ? "leader" : "walker") } }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    });
                    continue;
                }
                if (partial && word == "walk") continue;
                rows.Add(new JsonObject
                {
                    ["id"] = item["id"]!.DeepClone(),
                    ["result"] = JsonSerializer.SerializeToNode(new
                    {
                        certain = true,
                        entryKind = "word",
                        classificationBasis = "Fixture classification",
                        spokenText = word,
                        pronunciationHint = "Use the requested reading.",
                        requiredPhonetic = (string?)null,
                        phonetic = word == "lead" ? "liːd" : "wɔːk",
                        partsOfSpeech = data["writable"]!["partsOfSpeech"]!.AsArray().Select(id => new { meaningId = id!.GetValue<string>(), partOfSpeech = "v." }).ToArray(),
                        pronunciationControl = "instructions",
                        speechPhonetic = (string?)null
                    })
                });
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = single ? rows[0]!["result"]!.ToJsonString() : new JsonObject { ["results"] = rows }.ToJsonString() } } } }) };
        }
    }
}
