using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

/// <summary>Deletes only precisely authorized invalidated specific audio; receipts and tombstones remain permanent.</summary>
public sealed class AudioInvalidationRecoveryService(IAudioInvalidationRepository repository, IAudioObjectStore store,
    AudioRecoverySettings settings, TimeProvider clock)
{
    public async Task<AudioRecoveryResult> RunBatchAsync(CancellationToken token)
    {
        if (!settings.Enabled) return new(0, 0, 0, 0);
        var candidates = await repository.FindRecoverableAsync(Now(), settings.BatchSize, token);
        var expired = 0; var observed = 0; var failed = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.State == AudioInvalidationState.Claimed)
            {
                if (await repository.ExpireAsync(candidate.AssetId, Now(), RetryAt(candidate.Attempts), token)) expired++;
                continue;
            }
            var now = Now();
            var claim = await repository.ClaimAsync(candidate.AssetId, Guid.NewGuid().ToString(), now,
                now + settings.LeaseSeconds * 1000L, now + settings.RetrySeconds * 1000L, token);
            if (claim == null) continue;
            try
            {
                if (!Controlled(claim.Location)) throw new InvalidOperationException("Audio invalidation location is invalid.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                try { await store.RemoveAsync(claim.Location, deadline.Token).WaitAsync(deadline.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* Unknown Delete outcome: independently observe the exact key. */ }
                var remaining = await store.StatAsync(claim.Location, deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (remaining != null) throw new InvalidOperationException("Audio invalidation was not observed.");
                if (await repository.ObserveAsync(claim.AssetId, claim.Lease, Now(),
                    Now() + settings.RecheckSeconds * 1000L, token)) observed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failed++;
                await repository.ReleaseAsync(claim.AssetId, claim.Lease, Now(), RetryAt(claim.Attempts), token);
            }
        }
        return new(candidates.Count, expired, observed, failed);
    }
    private bool Controlled(AudioObjectLocation location)
    {
        if (location.Bucket != settings.Bucket || !location.Key.StartsWith(settings.KeyPrefix, StringComparison.Ordinal)) return false;
        var suffix = location.Key[settings.KeyPrefix.Length..];
        return suffix.Length == 36 && suffix.EndsWith(".mp3", StringComparison.Ordinal)
            && Guid.TryParseExact(suffix[..32], "N", out _) && suffix[..32] == suffix[..32].ToLowerInvariant();
    }
    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private long RetryAt(int attempts) => Now() + Math.Min(settings.MaxRetrySeconds,
        settings.RetrySeconds * (1L << Math.Clamp(attempts - 1, 0, 20))) * 1000;
}
