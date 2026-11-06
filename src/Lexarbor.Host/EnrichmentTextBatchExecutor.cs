using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Lexarbor.Service;

namespace Lexarbor.Host;

/// <summary>Stages several independently validated plans; existing executors retain publication and recovery ownership.</summary>
public sealed class EnrichmentTextBatchExecutor(IEnrichmentTaskRepository tasks, IEnrichmentExecutionRepository catalog,
    IVocabularyExpansionRepository expansions, IVocabularyBookRepository books, IAiRequestBatchRepository batches,
    AiTextBatchClient client, EnrichmentServiceGate gate, EnrichmentRuntimeConfiguration runtime,
    MeaningSubmissionSettings submissions, IAudioUploadOperationFactory storage, TimeProvider clock)
{
    private sealed record Work(EnrichmentTask Task, AiTextItem<AiBatchResult> Item);
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<IReadOnlyList<string>> PrepareAsync(IReadOnlyList<string> due, CancellationToken token)
    {
        var snapshot = runtime.Current;
        var selected = new List<string>();
        var words = new HashSet<string>(StringComparer.Ordinal);
        var work = new List<Work>();
        using var operation = await storage.AcquireAsync(token);
        foreach (var id in due)
        {
            var original = await tasks.GetAsync(id, token);
            if (original == null || !words.Add(original.Target.WordId)) continue;
            selected.Add(id);
            if (!snapshot.CanPrepare) continue;
            var key = original.Target;
            if (key.Step != EnrichmentStep.Expand)
            {
                if (key.Step == EnrichmentStep.Audio && (!operation.Settings.Enabled
                    || (await tasks.ReadInputAsync(key, token))?.OwnAssetId != null)) continue;
                key = key with { Step = EnrichmentStep.Prepare };
            }
            var task = await tasks.EnsureAsync(key, EnrichmentTargetExecutor.ConfigurationVersion(key, snapshot), Now,
                token, snapshot.Enrichment.FailureBudget);
            if (task == null) continue;
            var round = task.Rounds.Single(r => r.Number == task.CurrentRound);
            if (round.State is not (EnrichmentState.Pending or EnrichmentState.Waiting) || round.NextAttemptAtUnixMs > Now
                || round.Plan != null || task.Calls.Any(c => c.Round == round.Number && c.Result == EnrichmentCallResult.Started)) continue;
            var call = Guid.NewGuid().ToString();
            if (key.Step == EnrichmentStep.Expand)
            {
                if (string.IsNullOrWhiteSpace(submissions.BookId) || await books.GetByIdAsync(submissions.BookId, token) is not { Status: true }) continue;
                var input = await expansions.ReadAsync(key, token);
                if (input == null || input.Version != round.Input) continue;
                var item = VocabularyExpansionClient.BatchItem(call, input, snapshot.Enrichment.Text, submissions.BookId);
                work.Add(new(task, new(call, item.Prompt, content =>
                {
                    var parsed = item.Validate(content);
                    return new(call, parsed.Plan == null ? null : new(input.Word, null, null, null, Expansion: parsed.Plan), ErrorCode: parsed.ErrorCode);
                })));
            }
            else
            {
                var input = await catalog.ReadAsync(key, token);
                if (input == null || input.Request.InputVersion != round.Input) continue;
                var item = OpenAiCompatiblePronunciationPreparationClient.BatchItem(call, input.Request, snapshot);
                if (item == null) continue;
                work.Add(new(task, new(call, item.Prompt, content =>
                {
                    var parsed = item.Validate(content);
                    return new(call, parsed.Plan == null ? null : EnrichmentPreparedPlans.Store(parsed.Plan, input.OwnAssetId), ErrorCode: parsed.ErrorCode == null ? null : "text." + parsed.ErrorCode);
                })));
            }
        }
        foreach (var group in work.GroupBy(w => w.Task.Target.Step))
        {
            var kind = group.Key.ToString();
            var instructions = group.Key == EnrichmentStep.Expand ? VocabularyExpansionClient.Instructions : OpenAiCompatiblePronunciationPreparationClient.Instructions;
            var limit = await batches.LimitAsync(kind, snapshot.TextVersion!, group.Key == EnrichmentStep.Expand ? 3 : 6, token);
            var queue = group.ToArray();
            for (var index = 0; index < queue.Length;)
            {
                var chunk = queue.Skip(index).Take(limit).ToArray();
                while (chunk.Length > 0 && AiTextBatchClient.Body(snapshot.Enrichment.Text, instructions, chunk.Select(w => w.Item).ToArray()).Length > AiTextBatchClient.MaximumBytes)
                    chunk = chunk.Take(chunk.Length - 1).ToArray();
                if (chunk.Length == 0)
                {
                    var oversized = await tasks.ClaimAsync(queue[index++].Task.Id, "enrichment-text-batch", Now, Now + 900_000, token);
                    if (oversized != null) await tasks.StopAsync(oversized, "batch.input_too_large", Now, token);
                    continue;
                }
                index += chunk.Length;
                if (runtime.Current.TextVersion != snapshot.TextVersion) return selected;
                var service = await gate.EnterAsync("text", false, token);
                if (service.LeaseId == null) return selected;
                var members = new List<AiBatchMember>();
                foreach (var item in chunk)
                {
                    var guard = await tasks.ClaimAsync(item.Task.Id, "enrichment-text-batch", Now, Now + 900_000, token);
                    if (guard != null) members.Add(new(item.Item.Id, guard));
                }
                var receipt = members.Count == 0 ? null : await batches.BeginAsync(kind, snapshot.TextVersion!, members, Now, token);
                if (receipt == null) { await gate.FinishAsync(service, "Superseded", null, false, null, token); continue; }
                var ids = receipt.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                var response = await client.SendAsync(snapshot.Enrichment.Text, instructions, chunk.Where(w => ids.Contains(w.Item.Id)).Select(w => w.Item).ToArray(), token);
                var valid = response.Items.Values.Count(r => r.Plan != null);
                var outcome = response.Outcome == "Ready" && valid != receipt.Members.Count ? "Partial" : response.Outcome;
                if (outcome is "Invalid" or "Partial" || response.ErrorCode == "batch.timeout") limit = Math.Max(1, limit / 2);
                var settings = snapshot.Enrichment;
                var retryAt = Now + Math.Max(1000, (long)(response.RetryAfter?.TotalMilliseconds ?? (outcome == "ServiceFailure" ? 30000 : 1000)));
                await batches.CompleteAsync(receipt, response.Items.Values.ToArray(), outcome, response.ErrorCode, limit, Now, retryAt,
                    CancellationToken.None, service, new(settings.ServiceFailureThreshold, settings.ServicePauseSeconds,
                        settings.MaxServicePauseSeconds, settings.ThrottleInitialSeconds, settings.ThrottleMaxSeconds));
                if (outcome is "Throttled" or "ServiceFailure" or "Rejected") return selected;
            }
        }
        return selected;
    }
}
