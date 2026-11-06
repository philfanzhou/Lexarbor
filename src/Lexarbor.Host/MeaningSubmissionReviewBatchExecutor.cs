using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;

namespace Lexarbor.Host;

public sealed class MeaningSubmissionReviewBatchExecutor(IServiceScopeFactory scopes, EnrichmentRuntimeConfiguration runtime,
    MeaningSubmissionSettings settings, AiTextBatchClient client, AutomaticMeaningSubmissionReviewClient validator, TimeProvider clock)
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private sealed record Entry(MeaningSubmissionReviewLease Lease, AiTextItem<AutomaticReviewResponse>? Item,
        MeaningSubmissionAiReview? Saved, string? Failure = null);

    public async Task<int> RunAsync(CancellationToken token)
    {
        var snapshot = runtime.Current;
        if (MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(snapshot.ReviewAssistant, settings) != null) return 0;
        await using var scope = scopes.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMeaningSubmissionAutoReviewRepository>();
        var submissions = scope.ServiceProvider.GetRequiredService<IMeaningSubmissionRepository>();
        var batches = scope.ServiceProvider.GetRequiredService<IAiRequestBatchRepository>();
        var options = snapshot.ReviewAssistant;
        var version = snapshot.ReviewVersion ?? "review.configuration.missing";
        var limit = await batches.LimitAsync("Review", version, 6, token);
        var entries = new List<Entry>();
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        var response = new AiTextResult<AutomaticReviewResponse>("Ready", new Dictionary<string, AutomaticReviewResponse>());
        var clientSettings = new EnrichmentClientSettings(options.Endpoint!, options.Model!, options.ApiKey ?? "", options.TimeoutSeconds);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watchStop = new CancellationTokenSource();
        var watch = WatchAsync(snapshot, operation, watchStop.Token);
        try
        {
            for (var i = 0; i < limit; i++)
            {
                operation.Token.ThrowIfCancellationRequested();
                var lease = await repository.ClaimAsync(_owner, TimeSpan.FromSeconds(options.TimeoutSeconds + 60), operation.Token);
                if (lease == null) break;
                entries.Add(new(lease, null, null)); // The finally block owns every acquired lease, including failed reads.
                var saved = await repository.ReadValidatedAsync(lease, operation.Token);
                var submission = saved == null ? await submissions.GetByIdAsync(lease.SubmissionId, operation.Token) : null;
                var item = submission == null ? null : validator.BatchItem(submission, lease.AttemptId, options);
                entries[^1] = new(lease, item, saved);
                var planned = entries.Where(e => e.Item != null).Select(e => e.Item!).ToArray();
                if (planned.Length > 0 && AiTextBatchClient.Body(clientSettings, AutomaticMeaningSubmissionReviewClient.Instructions, planned).Length > AiTextBatchClient.MaximumBytes)
                {
                    // Defer the last oversized member; never charge a request that was not sent.
                    entries[^1] = new(lease, null, saved, planned.Length == 1 ? "response_too_large" : null);
                    break;
                }
            }
            var pending = entries.Where(e => e.Item != null).ToArray();
            if (pending.Length > 0)
            {
                if (MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(runtime.Current.ReviewAssistant, settings) != null)
                { await operation.CancelAsync(); return entries.Count; }
                operation.Token.ThrowIfCancellationRequested();
                var receipt = await batches.BeginAsync("Review", version, pending.Select(e => new AiBatchMember(e.Lease.AttemptId, Review: e.Lease)).ToArray(), Now, operation.Token);
                if (receipt != null)
                {
                    var admitted = receipt.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                    response = await client.SendAsync(clientSettings, AutomaticMeaningSubmissionReviewClient.Instructions,
                        pending.Where(e => admitted.Contains(e.Lease.AttemptId)).Select(e => e.Item!).ToArray(), operation.Token);
                    var valid = response.Items.Values.Count(r => r.Review != null);
                    var outcome = response.Outcome == "Ready" && valid != receipt.Members.Count ? "Partial" : response.Outcome;
                    var next = outcome is "Partial" or "Invalid" || response.ErrorCode == "batch.timeout" ? Math.Max(1, limit / 2) : limit;
                    await batches.CompleteAsync(receipt, response.Items.Select(p => new AiBatchResult(p.Key, Review: p.Value.Review,
                        ErrorCode: p.Value.FailureCode)).ToArray(), outcome, response.ErrorCode, next, Now, null, CancellationToken.None);
                }
            }
            foreach (var entry in entries)
            {
                var review = entry.Saved ?? (response.Items.TryGetValue(entry.Lease.AttemptId, out var item) ? item.Review : null);
                if (review == null) continue;
                try
                {
                    try
                    {
                        await ApplyAsync(review, entry.Lease, () => !operation.IsCancellationRequested
                        && MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(runtime.Current.ReviewAssistant, settings) == null, operation.Token);
                    }
                    catch (OperationCanceledException) when (operation.IsCancellationRequested)
                    {
                        await ApplyAsync(review, entry.Lease, () => false, CancellationToken.None);
                    }
                }
                catch (Exception) { failures[entry.Lease.AttemptId] = "result_commit_failed"; }
            }
            return entries.Count;
        }
        catch (Exception) when (!operation.IsCancellationRequested)
        {
            foreach (var entry in entries) failures.TryAdd(entry.Lease.AttemptId, "result_commit_failed");
            throw;
        }
        finally
        {
            await watchStop.CancelAsync(); await watch;
            foreach (var entry in entries)
            {
                var failure = failures.GetValueOrDefault(entry.Lease.AttemptId) ?? entry.Failure ?? (operation.IsCancellationRequested || entry.Saved != null || entry.Item == null ? null
                    : response.Items.TryGetValue(entry.Lease.AttemptId, out var result) && result.Review != null ? null
                    : response.Outcome switch
                    {
                        "Throttled" => "rate_limited",
                        "Rejected" => "request_rejected",
                        "ServiceFailure" => "service_unavailable",
                        _ => "invalid_response"
                    });
                await repository.FinishAsync(entry.Lease, failure, response.RetryAfter, CancellationToken.None);
            }
        }
    }

    private async Task ApplyAsync(MeaningSubmissionAiReview review, MeaningSubmissionReviewLease lease, Func<bool> eligible, CancellationToken token)
    {
        // Each publication owns a fresh context: a failed/cancelled write cannot contaminate its siblings.
        await using var publication = scopes.CreateAsyncScope();
        var repository = publication.ServiceProvider.GetRequiredService<IMeaningSubmissionAutoReviewRepository>();
        await publication.ServiceProvider.GetRequiredService<MeaningSubmissionService>()
            .ApplyAiReviewAsync(review, new Eligibility(repository, lease, eligible), token);
    }

    private async Task WatchAsync(EnrichmentConfigurationSnapshot snapshot, CancellationTokenSource operation, CancellationToken token)
    {
        try
        {
            while (true)
            {
                snapshot = await runtime.WaitForChangeAsync(snapshot, token);
                if (MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(snapshot.ReviewAssistant, settings) != null)
                { await operation.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private sealed class Eligibility(IMeaningSubmissionAutoReviewRepository repository, MeaningSubmissionReviewLease lease, Func<bool> enabled)
        : IMeaningSubmissionReviewEligibility
    {
        public Task<bool> CanApplyAsync(string submissionId, string attemptId, CancellationToken token) =>
            submissionId == lease.SubmissionId && attemptId == lease.AttemptId && enabled()
                ? repository.IsCurrentAsync(lease, token) : Task.FromResult(false);
    }
}
