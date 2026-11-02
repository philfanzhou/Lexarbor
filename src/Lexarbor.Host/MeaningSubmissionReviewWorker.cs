using System.Threading.Channels;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;

namespace Lexarbor.Host;

public sealed class MeaningSubmissionReviewNotifications : IMeaningSubmissionChangeNotifier
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    public void Notify() => _channel.Writer.TryWrite(true);
    public void Drain() { while (_channel.Reader.TryRead(out _)) { } }
    public Task WaitAsync(CancellationToken token) => _channel.Reader.WaitToReadAsync(token).AsTask();
}

public sealed class MeaningSubmissionAutomaticReviewStatusProvider(EnrichmentRuntimeConfiguration runtime,
    MeaningSubmissionSettings settings, IMeaningSubmissionAutoReviewRepository repository) : IMeaningSubmissionAutomaticReviewStatusProvider
{
    public async Task<MeaningSubmissionAutomaticReviewStatus> ReadAsync(CancellationToken token)
    {
        var options = runtime.Current.ReviewAssistant;
        var code = PauseCode(options, settings);
        var backlog = await repository.ReadBacklogAsync(token);
        return new(options.AutoReviewEnabled, code != null, code, backlog.PendingCount,
            backlog.LastFailureCode, backlog.LastFailureAt, backlog.NextAttemptAt);
    }
    internal static string? PauseCode(ReviewAssistantOptions options, MeaningSubmissionSettings settings) =>
        !options.AutoReviewEnabled ? "disabled" : !settings.IsConfigured ? "submissions_not_configured"
        : !options.IsEnabled ? "configuration_missing" : !options.CanAutomaticallyReview ? "configuration_invalid" : null;
}

/// <summary>One bounded queue, one concurrent call, no shared enrichment pause or failure budget.</summary>
public sealed class MeaningSubmissionReviewWorker(IServiceScopeFactory scopes, EnrichmentRuntimeConfiguration runtime,
    MeaningSubmissionSettings settings, MeaningSubmissionReviewNotifications notifications, TimeProvider clock,
    AutomaticMeaningSubmissionReviewClient client, ILogger<MeaningSubmissionReviewWorker> logger) : BackgroundService
{
    public const int BatchSize = 20;
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    private readonly string _owner = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Program completes migrations/configuration before starting hosted services.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var observed = runtime.Current;
                try { await RunBatchAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Automatic review scheduling failed; pending submissions will be retried."); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    await await Task.WhenAny(Task.Delay(ScanInterval, clock, wait.Token),
                        runtime.WaitForChangeAsync(observed, wait.Token), notifications.WaitAsync(wait.Token));
                }
                finally { await wait.CancelAsync(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<int> RunBatchAsync(CancellationToken token)
    {
        notifications.Drain();
        var count = 0;
        for (; count < BatchSize; count++)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = runtime.Current;
            var options = snapshot.ReviewAssistant;
            if (MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(options, settings) != null) break;
            await using var scope = scopes.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IMeaningSubmissionAutoReviewRepository>();
            var lease = await repository.ClaimAsync(_owner, TimeSpan.FromSeconds(options.TimeoutSeconds + 30), token);
            if (lease == null) break;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var watcherStop = new CancellationTokenSource();
            var watcher = WatchPauseAsync(snapshot, operation, watcherStop.Token);
            string? failure = null; TimeSpan? retry = null;
            try
            {
                // Recheck after acquiring the short writer; no request is sent while paused.
                if (MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(runtime.Current.ReviewAssistant, settings) != null) continue;
                var submission = await scope.ServiceProvider.GetRequiredService<IMeaningSubmissionRepository>().GetByIdAsync(lease.SubmissionId, operation.Token);
                if (submission == null) continue;
                var validated = await repository.ReadValidatedAsync(lease, operation.Token);
                var result = validated == null ? await client.ReviewAsync(submission, lease.AttemptId, options, operation.Token) : new AutomaticReviewResponse(validated);
                if (result.Review is { } review)
                {
                    // Only the validated, bounded domain fields are staged, never the provider
                    // envelope. A process restart after an application failure resumes this exact
                    // attempt/model/time without another supplier call or a changed payload.
                    await repository.StageValidatedAsync(lease, review, CancellationToken.None);
                    // The actual call's model/time travel with the validated result. A valid late
                    // conclusion still gets a permanent Applied=false record under the same writer.
                    var service = scope.ServiceProvider.GetRequiredService<MeaningSubmissionService>();
                    var eligibility = new Eligibility(repository, lease, () => !operation.IsCancellationRequested
                        && MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(runtime.Current.ReviewAssistant, settings) == null);
                    try { await service.ApplyAiReviewAsync(review, eligibility, operation.Token); }
                    catch (OperationCanceledException) when (operation.IsCancellationRequested)
                    {
                        // Roll back cancellation before commit, then retain this already validated
                        // payload. An ambiguous committed outcome replays its original fact.
                        await using var recovery = scopes.CreateAsyncScope();
                        var recoveryRepository = recovery.ServiceProvider.GetRequiredService<IMeaningSubmissionAutoReviewRepository>();
                        await recovery.ServiceProvider.GetRequiredService<MeaningSubmissionService>().ApplyAiReviewAsync(review,
                            new Eligibility(recoveryRepository, lease, () => false), CancellationToken.None);
                    }
                }
                else { failure = result.FailureCode; retry = result.RetryAfter; }
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            catch (Exception) { failure = "result_commit_failed"; logger.LogWarning("Automatic review could not commit; the pending submission will be retried."); }
            finally
            {
                await watcherStop.CancelAsync();
                await watcher;
                // A cancellation is a pause, never a consumed failure. A stale owner cannot
                // release a reclaimed lease. A crash here recovers through its persisted expiry.
                await repository.FinishAsync(lease, operation.IsCancellationRequested ? null : failure, retry, CancellationToken.None);
            }
        }
        return count;
    }

    private async Task WatchPauseAsync(EnrichmentConfigurationSnapshot observed, CancellationTokenSource operation, CancellationToken token)
    {
        try
        {
            while (true)
            {
                observed = await runtime.WaitForChangeAsync(observed, token);
                if (MeaningSubmissionAutomaticReviewStatusProvider.PauseCode(observed.ReviewAssistant, settings) == null) continue;
                await operation.CancelAsync(); return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private sealed class Eligibility(IMeaningSubmissionAutoReviewRepository repository, MeaningSubmissionReviewLease lease, Func<bool> enabled)
        : IMeaningSubmissionReviewEligibility
    {
        public async Task<bool> CanApplyAsync(string submissionId, string attemptId, CancellationToken token) =>
            submissionId == lease.SubmissionId && attemptId == lease.AttemptId && enabled() && await repository.IsCurrentAsync(lease, token);
    }
}
