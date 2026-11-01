using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;

namespace Lexarbor.Host.Audio;

public sealed class AudioRecoveryWorker(IServiceScopeFactory scopes, AudioRuntimeConfiguration runtime,
    TimeProvider clock, ILogger<AudioRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Host startup completes independently of storage availability.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                AudioRuntimeSnapshot snapshot;
                using (var operation = await runtime.AcquireAsync(stoppingToken))
                {
                    snapshot = operation.Snapshot;
                    if (snapshot.Enabled)
                    {
                        try
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            var service = new AudioRecoveryService(scope.ServiceProvider.GetRequiredService<IAudioUploadRepository>(),
                                new RecoveryStore(operation), new(true, snapshot.Bucket, snapshot.KeyPrefix, snapshot.RecoveryBatchSize,
                                    snapshot.OperationTimeoutSeconds, snapshot.RecoveryLeaseSeconds, snapshot.RecoveryRetrySeconds,
                                    snapshot.RecoveryMaxRetrySeconds, snapshot.RecoveryRecheckSeconds), clock);
                            var result = await service.RunBatchAsync(stoppingToken);
                            // Each queue gets a bounded batch so abandoned uploads
                            // cannot starve explicitly authorized invalidations.
                            var invalidations = new AudioInvalidationRecoveryService(
                                scope.ServiceProvider.GetRequiredService<IAudioInvalidationRepository>(), new RecoveryStore(operation),
                                new(true, snapshot.Bucket, snapshot.KeyPrefix, snapshot.RecoveryBatchSize,
                                    snapshot.OperationTimeoutSeconds, snapshot.RecoveryLeaseSeconds, snapshot.RecoveryRetrySeconds,
                                    snapshot.RecoveryMaxRetrySeconds, snapshot.RecoveryRecheckSeconds), clock);
                            var invalidated = await invalidations.RunBatchAsync(stoppingToken);
                            if (invalidated.Failed > 0) logger.LogWarning("Audio invalidation will retry {Count} object operations.", invalidated.Failed);
                            if (result.Failed > 0) logger.LogWarning("Audio recovery will retry {Count} object operations.", result.Failed);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                        catch (Exception) { logger.LogWarning("Audio recovery batch failed; it will retry after the configured interval."); }
                    }
                }
                if (!snapshot.Enabled)
                    await runtime.WaitForEnableAsync(snapshot.Generation, stoppingToken);
                else
                    await WaitForNextBatchAsync(snapshot, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task WaitForNextBatchAsync(AudioRuntimeSnapshot snapshot, CancellationToken stoppingToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var interval = Task.Delay(TimeSpan.FromSeconds(snapshot.RecoveryIntervalSeconds), clock, wait.Token);
        try
        {
            var enabled = runtime.WaitForEnableAsync(snapshot.Generation, wait.Token);
            var next = await Task.WhenAny(interval, enabled);
            await next;
            // Credential rotations and repeated enabled publications cannot skip the interval.
        }
        finally { await wait.CancelAsync(); }
    }

    // Domain deadlines may end a batch's wait before an uncooperative provider ends.
    // Keep its same-generation client alive until the actual Delete/Stat task ends.
    private sealed class RecoveryStore(AudioOperationReference operation) : IAudioObjectStore
    {
        public async Task RemoveAsync(AudioObjectLocation location, CancellationToken token)
        {
            using var call = operation.Retain();
            await call.ObjectStore.RemoveAsync(location, token);
        }
        public async Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token)
        {
            using var call = operation.Retain();
            return await call.ObjectStore.StatAsync(location, token);
        }
        public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, CancellationToken token)
            => throw new NotSupportedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token)
            => throw new NotSupportedException();
    }
}
