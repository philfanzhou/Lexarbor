using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;

namespace Lexarbor.Host;

public sealed class VocabularyExpansionExecutor(IEnrichmentTaskRepository tasks, IVocabularyExpansionRepository expansions,
    IVocabularyBookRepository books, VocabularyExpansionService publisher, VocabularyExpansionClient client,
    EnrichmentServiceGate gate, EnrichmentRuntimeConfiguration runtime, MeaningSubmissionSettings submissions, TimeProvider clock)
{
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<EnrichmentTask?> ExecuteAsync(EnrichmentTask task, string owner, CancellationToken token)
    {
        var snapshot = runtime.Current;
        task = (await tasks.EnsureAsync(task.Target, EnrichmentTargetExecutor.ConfigurationVersion(task.Target, snapshot),
            Now, token, snapshot.Enrichment.FailureBudget))!;
        if (task == null) return null;
        var guard = await tasks.ClaimAsync(task.Id, owner, Now, Now + 900_000, token);
        if (guard == null) return await tasks.GetAsync(task.Id, token);
        var round = task.Rounds.Single(r => r.Number == guard.Round);
        var plan = round.Plan?.Expansion;
        var open = task.Calls.SingleOrDefault(c => c.Round == guard.Round && c.Result == EnrichmentCallResult.Started);
        if (open != null)
        {
            await tasks.FinishCallAsync(guard, open.Id, plan != null ? EnrichmentCallResult.Succeeded : EnrichmentCallResult.TransientFailure,
                plan != null ? null : "expansion.outcome_unknown", Now, plan != null ? null : Now + 30_000, token);
            if (plan == null) return await tasks.GetAsync(task.Id, token);
        }
        var bookId = submissions.BookId;
        if (!snapshot.CanPrepare || string.IsNullOrWhiteSpace(bookId)
            || await books.GetByIdAsync(bookId, token) is not { Status: true })
            return await Wait(EnrichmentWaitReason.ConfigurationMissing, Now + 30_000);
        var input = await expansions.ReadAsync(task.Target, token);
        if (input?.Version != guard.Input) return await tasks.GetAsync(task.Id, token);
        if (plan == null)
        {
            var service = await gate.EnterAsync("text", false, token);
            if (service.LeaseId == null) return await Wait(service.WaitReason ?? EnrichmentWaitReason.Throttled, service.NextAttemptAtUnixMs);
            await using var cleanup = gate.Observe(service);
            var callId = Guid.NewGuid().ToString();
            var result = await client.GenerateAsync(input!, snapshot.Enrichment.Text, bookId, token, async ct =>
                runtime.Current.TextVersion == snapshot.TextVersion
                && await tasks.BeginCallAsync(guard, callId, "text", null, Now, ct),
                Math.Max(round.ConsumedFailures, round.RecoveryId == null ? 0 : 1));
            cleanup.Record(result.Outcome, result.ErrorCode, result.RequestMade, result.RetryAfter);
            if (!result.RequestMade)
            {
                await cleanup.FinishAsync();
                if (result.Outcome == "InvalidPlan")
                {
                    await tasks.StopAsync(guard, result.ErrorCode!, Now, token);
                    return await tasks.GetAsync(task.Id, token);
                }
                return await Wait(EnrichmentWaitReason.ConfigurationMissing, Now + 1000);
            }
            if (result.Plan != null)
            {
                // Stage before acknowledging success. Restart can publish the exact validated result without billing again.
                plan = result.Plan;
                if (!await tasks.SavePlanAsync(guard, new(input!.Word, null, null, null, Expansion: plan), Now, token))
                {
                    await cleanup.FinishAsync();
                    return await tasks.GetAsync(task.Id, token);
                }
                await cleanup.FinishAsync();
                if (!await tasks.FinishCallAsync(guard, callId, EnrichmentCallResult.Succeeded, null, Now, null, token))
                    return await tasks.GetAsync(task.Id, token);
            }
            else
            {
                var outcome = EnrichmentRecoveryPolicy.Failure(result.Outcome, result.ErrorCode);
                var retry = Now + Math.Max(30_000, (long)(result.RetryAfter?.TotalMilliseconds ?? 0));
                await cleanup.FinishAsync(new(guard, callId, outcome, result.ErrorCode!, outcome == EnrichmentCallResult.TerminalFailure ? null : retry));
                return await tasks.GetAsync(task.Id, token);
            }
        }
        if (plan.BookId != bookId)
        {
            await tasks.StopAsync(guard, "expansion.destination_changed", Now, token);
            return await tasks.GetAsync(task.Id, token);
        }
        await publisher.PublishAsync(task, guard, plan,
            () => runtime.Current.CanPrepare && runtime.Current.TextVersion == snapshot.TextVersion, token);
        return await tasks.GetAsync(task.Id, token);

        async Task<EnrichmentTask?> Wait(EnrichmentWaitReason reason, long next)
        {
            await tasks.WaitAsync(guard, reason, Now, Math.Max(Now + 1000, next), token);
            return await tasks.GetAsync(task.Id, token);
        }
    }
}
