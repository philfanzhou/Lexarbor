using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed record AudioRecoverySettings(bool Enabled, string Bucket, string KeyPrefix, int BatchSize = 20,
    int TimeoutSeconds = 60, int LeaseSeconds = 300, int RetrySeconds = 60, int MaxRetrySeconds = 3600,
    int RecheckSeconds = 86400);
public sealed record AudioRecoveryResult(int Examined, int Expired, int Observed, int Failed);

/// <summary>Reclaims only database-authorized never-published objects. Tombstones and receipts are permanent.</summary>
public sealed class AudioRecoveryService(IAudioUploadRepository repository, IAudioObjectStore store,
    AudioRecoverySettings settings, TimeProvider clock)
{
    public async Task<AudioRecoveryResult> RunBatchAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return new(0, 0, 0, 0);
        var candidates = await repository.FindRecoverableAsync(Now(), settings.BatchSize, cancellationToken);
        var expired = 0; var observed = 0; var failed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = candidate.Operation.IdempotencyKey;
            if (candidate.Operation.State is AudioUploadState.Reserved or AudioUploadState.Transferring)
            {
                if (await repository.ExpireAsync(key, Now(), cancellationToken)) expired++;
            }
            else if (candidate.Operation.State == AudioUploadState.CleanupClaimed)
            {
                if (await repository.ExpireCleanupAsync(key, Now(), AudioRecoveryPolicy.RetryAt(candidate.Operation.Cleanup.Attempts, settings, Now()), cancellationToken)) expired++;
                continue;
            }
            var now = Now();
            var claim = await repository.ClaimCleanupAsync(key, Guid.NewGuid().ToString(), now,
                now + settings.LeaseSeconds * 1000L, cancellationToken);
            if (claim == null) continue;
            // Never infer eligibility from a missing binding or scan a bucket. The
            // CAS above rechecks publication, upload lease, age and retry time.
            try
            {
                if (!AudioRecoveryPolicy.IsControlled(claim.Asset.Location, settings)) throw new InvalidOperationException("Audio recovery location is invalid.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                await store.RemoveAsync(claim.Asset.Location, deadline.Token).WaitAsync(deadline.Token);
                var remaining = await store.StatAsync(claim.Asset.Location, deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (remaining != null) throw new InvalidOperationException("Audio cleanup was not observed.");
                if (await repository.ObserveCleanupAsync(key, claim.Operation.Cleanup.Lease, Now(),
                    Now() + settings.RecheckSeconds * 1000L, cancellationToken)) observed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Do not start uncancellable IO during shutdown. The persisted
                // cleanup lease expires and another process resumes this record.
                throw;
            }
            catch (Exception)
            {
                // No provider error, URL, endpoint or credential crosses this boundary.
                failed++;
                await repository.ReleaseCleanupAsync(key, claim.Operation.Cleanup.Lease, Now(),
                    AudioRecoveryPolicy.RetryAt(claim.Operation.Cleanup.Attempts, settings, Now()), "audio.cleanup_failed", cancellationToken);
            }
        }
        return new(candidates.Count, expired, observed, failed);
    }

    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
}
