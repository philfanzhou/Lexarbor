using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed record AudioRecoverySettings(bool Enabled, string Bucket, string KeyPrefix, int BatchSize = 20,
    int TimeoutSeconds = 60, int LeaseSeconds = 300, int RetrySeconds = 60, int MaxRetrySeconds = 3600,
    int RecheckSeconds = 86400);
public sealed record AudioRecoveryResult(int Examined, int Expired, int Observed, int Failed);

/// <summary>Reclaims only database-authorized never-published objects. Tombstones and receipts are permanent.</summary>
public sealed class AudioRecoveryService
{
    private readonly RecoverySource[] sources;
    private readonly IAudioObjectStore store;
    private readonly AudioRecoverySettings settings;
    private readonly TimeProvider clock;

    public AudioRecoveryService(IAudioUploadRepository repository, IAudioObjectStore store,
        AudioRecoverySettings settings, TimeProvider clock) : this([new VocabularyRecoverySource(repository)], store, settings, clock) { }

    public AudioRecoveryService(ITextbookAudioUploadRepository repository, IAudioObjectStore store,
        AudioRecoverySettings settings, TimeProvider clock) : this([new TextbookRecoverySource(repository)], store, settings, clock) { }

    public AudioRecoveryService(IAudioUploadRepository vocabulary, ITextbookAudioUploadRepository textbook,
        IAudioObjectStore store, AudioRecoverySettings settings, TimeProvider clock)
        : this([new VocabularyRecoverySource(vocabulary), new TextbookRecoverySource(textbook)], store, settings, clock) { }

    private AudioRecoveryService(RecoverySource[] sources, IAudioObjectStore store, AudioRecoverySettings settings, TimeProvider clock)
    { this.sources = sources; this.store = store; this.settings = settings; this.clock = clock; }
    public async Task<AudioRecoveryResult> RunBatchAsync(CancellationToken cancellationToken, bool textbookFirst = false)
    {
        if (!settings.Enabled) return new(0, 0, 0, 0);
        // Each query is independently bounded. Materialize both before IO, then spend
        // one shared processing budget, alternating types without changing their order.
        var queues = new Queue<RecoveryUpload>[sources.Length];
        for (var i = 0; i < sources.Length; i++)
            queues[i] = new(await sources[i].FindAsync(Now(), settings.BatchSize, cancellationToken));
        var candidates = new List<(RecoverySource Repository, RecoveryUpload Upload)>();
        var turn = textbookFirst && sources.Length > 1 ? 1 : 0;
        while (candidates.Count < settings.BatchSize && queues.Any(q => q.Count > 0))
        {
            if (queues[turn].TryDequeue(out var upload)) candidates.Add((sources[turn], upload));
            turn = (turn + 1) % queues.Length;
        }
        var expired = 0; var observed = 0; var failed = 0;
        foreach (var (repository, candidate) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = candidate.Key;
            if (candidate.State is AudioUploadState.Reserved or AudioUploadState.Transferring)
            {
                if (await repository.ExpireAsync(key, Now(), cancellationToken)) expired++;
            }
            else if (candidate.State == AudioUploadState.CleanupClaimed)
            {
                if (await repository.ExpireCleanupAsync(key, Now(), RetryAt(candidate.Cleanup.Attempts), cancellationToken)) expired++;
                continue;
            }
            var now = Now();
            var claim = await repository.ClaimAsync(key, Guid.NewGuid().ToString(), now,
                now + settings.LeaseSeconds * 1000L, cancellationToken);
            if (claim == null) continue;
            // Never infer eligibility from a missing binding or scan a bucket. The
            // CAS above rechecks publication, upload lease, age and retry time.
            try
            {
                if (!Controlled(claim.Asset.Location)) throw new InvalidOperationException("Audio recovery location is invalid.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                await store.RemoveAsync(claim.Asset.Location, deadline.Token).WaitAsync(deadline.Token);
                var remaining = await store.StatAsync(claim.Asset.Location, deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (remaining != null) throw new InvalidOperationException("Audio cleanup was not observed.");
                if (await repository.ObserveAsync(key, claim.Cleanup.Lease, Now(),
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
                await repository.ReleaseAsync(key, claim.Cleanup.Lease, Now(),
                    RetryAt(claim.Cleanup.Attempts), "audio.cleanup_failed", cancellationToken);
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
