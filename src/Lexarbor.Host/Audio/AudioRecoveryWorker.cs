using Lexarbor.Domain.Services;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Audio;

public sealed class AudioRecoveryWorker(IServiceScopeFactory scopes, IOptions<AudioOptions> options,
    TimeProvider clock, ILogger<AudioRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        // Host startup completes independently of storage availability.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<AudioRecoveryService>().RunBatchAsync(stoppingToken);
                if (result.Failed > 0) logger.LogWarning("Audio recovery will retry {Count} object operations.", result.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Audio recovery batch failed; it will retry after the configured interval."); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.RecoveryIntervalSeconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
