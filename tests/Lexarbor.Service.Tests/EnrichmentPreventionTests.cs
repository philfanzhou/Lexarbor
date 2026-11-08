using System.Text.Json.Nodes;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

public sealed class EnrichmentTextRequestTests
{
    [Theory]
    [InlineData("https://api.minimax.cn/v1", "MiniMax-M3.1-Flash", true)]
    [InlineData("https://api.minimax.io/v1/", "MiniMax-M3.1-Flash", true)]
    [InlineData("https://api.minimax.io/v1", "other-model", false)]
    [InlineData("https://unrelated.example/v1", "MiniMax-M3.1-Flash", false)]
    public void FirstRequestBoundsKnownModelWithoutSendingItsOptionsToOtherProviders(string endpoint, string model, bool tuned)
    {
        var settings = new EnrichmentClientSettings(endpoint, model, "synthetic-test-key", 120);
        var body = JsonNode.Parse(EnrichmentTextRequest.Body(settings, "fixture", "{}", 6144))!;
        Assert.Equal(tuned, body["reasoning_effort"] != null);
        Assert.Equal(tuned, body["max_completion_tokens"] != null);
        if (tuned)
        {
            Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());
            Assert.Equal(6144, body["max_completion_tokens"]!.GetValue<int>());
        }
        Assert.DoesNotContain(settings.ApiKey, body.ToJsonString());
        var review = JsonNode.Parse(EnrichmentTextRequest.Body(settings, "review", "{}", 6144, false))!;
        Assert.Null(review["reasoning_effort"]);
    }

    [Theory]
    [InlineData("InvalidPlan", "text.phonetic_constraint_mismatch", EnrichmentCallResult.TransientFailure)]
    [InlineData("Uncertain", "text.pronunciation_uncertain", EnrichmentCallResult.TransientFailure)]
    [InlineData("InvalidPlan", "text.capability_unsupported", EnrichmentCallResult.TerminalFailure)]
    [InlineData("InvalidPlan", "tts.capability_unsupported", EnrichmentCallResult.TerminalFailure)]
    [InlineData("Rejected", "credentials_rejected", EnrichmentCallResult.TerminalFailure)]
    [InlineData("Throttled", "batch.throttled", EnrichmentCallResult.Throttled)]
    public void SharedFailurePolicyPreservesRealProviderRejections(string outcome, string error, EnrichmentCallResult expected)
        => Assert.Equal(expected, EnrichmentRecoveryPolicy.Failure(outcome, error));
}

public sealed partial class EnrichmentTargetExecutorTests
{
    [Fact]
    public async Task TrueAlternativeConflict_UsesTheSameFiniteBudgetWhenExecutionPathsAlternate()
    {
        await using var f = await Fixture.Create(fields: false, native: true);
        await f.Db.Vocabularies.ExecuteUpdateAsync(s => s.SetProperty(w => w.PhoneticUk, "liːd;led"), Ct);
        f.Http.Ipa = true; f.Http.Phonetic = "læd";
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        using var clients = new HttpFactory(f.Http);
        var scheduler = new EnrichmentSchedulerRepository(f.Db, f.Tasks);
        var batches = new AiRequestBatchRepository(f.Db, f.Tasks, new MeaningSubmissionAutoReviewRepository(f.Db, f.Clock), scheduler);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (attempt % 2 == 1) await BatchExecutor(f, f.Db, f.Tasks, scheduler, batches, clients).DispatchAsync([task.Id], Ct);
            else await f.Executor.ExecuteAsync(task.Id, "single", Ct);
            task = (await f.Tasks.GetAsync(task.Id, Ct))!;
            Assert.Equal(attempt, Round(task).ConsumedFailures);
            Assert.Equal("text.phonetic_constraint_mismatch", Round(task).ErrorCode);
            Assert.Equal(attempt == 5 ? EnrichmentState.Failed : EnrichmentState.Waiting, Round(task).State);
            if (attempt < 5) f.Clock.Now = Round(task).NextAttemptAtUnixMs!.Value + 1;
        }
        Assert.Equal(5, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
        Assert.Equal("liːd;led", await f.Db.Vocabularies.Select(w => w.PhoneticUk).SingleAsync(Ct));
    }

    [Fact]
    public async Task NewExpansion_BoundsFirstRequestAndShrinksAfterTimeoutAcrossRestarts()
    {
        await using var f = await Fixture.Create();
        await f.Publish(new() { ["Enrichment:Text:Endpoint"] = "https://api.minimax.cn/v1", ["Enrichment:Text:Model"] = "MiniMax-M3.1-Flash" });
        var ids = await ExpansionSources(f, 1);
        using var handler = new ExpansionBatchHandler { Failure = call => call <= 2 ? "timeout" : null };
        using var clients = new HttpFactory(handler);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var restarted = f.Open();
            var tasks = new EnrichmentTaskRepository(restarted, f.Clock);
            var scheduler = new EnrichmentSchedulerRepository(restarted, tasks);
            var batches = new AiRequestBatchRepository(restarted, tasks, new MeaningSubmissionAutoReviewRepository(restarted, f.Clock), scheduler);
            await BatchExecutor(f, restarted, tasks, scheduler, batches, clients).DispatchAsync(ids, Ct);
            var current = (await tasks.GetAsync(ids[0], Ct))!;
            Assert.Equal(Math.Min(attempt + 1, 2), Round(current).ConsumedFailures);
            if (attempt < 2) f.Clock.Now = Round(current).NextAttemptAtUnixMs!.Value + 1;
            else Assert.NotNull(Round(current).Plan!.Expansion);
        }
        Assert.Equal(3, handler.Calls);
        var bodies = handler.Bodies.Select(b => JsonNode.Parse(b)!).ToArray();
        Assert.All(bodies, b => Assert.Equal("low", b["reasoning_effort"]!.GetValue<string>()));
        Assert.Equal(new[] { 6144, 4096, 4096 }, bodies.Select(b => b["max_completion_tokens"]!.GetValue<int>()));
        var limits = bodies.Select(b => JsonNode.Parse(b["messages"]![1]!["content"]!.GetValue<string>())!["outputLimits"]!).ToArray();
        Assert.Equal(new[] { 12, 6, 3 }, limits.Select(l => l["candidates"]!.GetValue<int>()));
        Assert.Equal(new[] { 160, 0, 0 }, limits.Select(l => l["exampleCharacters"]!.GetValue<int>()));
        var task = (await f.Tasks.GetAsync(ids[0], Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round((await Expansion(f, f.Db, clients, "book").ExecuteAsync(task, "publish", Ct))!).State);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("text.phonetic_constraint_mismatch")]
    [InlineData("batch.timeout")]
    public async Task PreviousStrategyExhaustion_GetsOneNewStrategyAllowanceAndNoRestartLoop(string error)
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(error == "batch.timeout" ? ExpansionKey : Key(EnrichmentStep.Prepare));
        await FailLocally(f, task, error);
        var old = Round(task);
        var receipt = Guid.NewGuid().ToString();
        f.Db.EnrichmentRecoveries.Add(new()
        {
            Id = receipt,
            TargetId = task.Id,
            InputRevision = old.Input.Revision,
            ConfigurationVersion = old.ConfigurationVersion,
            SourceRound = task.CurrentRound,
            Reason = error,
            CreatedAtUnixMs = f.Clock.Now
        });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        await f.Db.EnrichmentRounds.Where(r => r.TargetId == task.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.RecoveryId, receipt), Ct);
        var configuration = await AdminConfig(f);
        if (error == "batch.timeout") configuration = configuration with { ExpansionBookId = "book" };
        Assert.True((await new EnrichmentAdminRepository(f.Db).DetailAsync(task.Id, 1, 10, configuration, f.Clock.Now, Ct))!.Task.AutomaticRecoveryAvailable);
        Assert.Equal(1, await f.Tasks.RecoverAsync(5, [], configuration, f.Clock.Now, Ct));
        task = (await f.Tasks.GetAsync(task.Id, Ct))!;
        Assert.Equal(2, task.CurrentRound);
        f.Clock.Now += 1001;
        await FailLocally(f, task, error);
        await using var restart = f.Open();
        var repository = new EnrichmentTaskRepository(restart, f.Clock);
        Assert.Equal(0, await repository.RecoverAsync(5, [], configuration, f.Clock.Now, Ct));
        Assert.False((await new EnrichmentAdminRepository(restart).DetailAsync(task.Id, 1, 10, configuration, f.Clock.Now, Ct))!.Task.AutomaticRecoveryAvailable);
        Assert.Equal(2, await restart.EnrichmentRecoveries.CountAsync(Ct));
    }
}
