namespace Lexarbor.Host.Authentication;

/// <summary>
/// Periodically reclaims expired administrator session rows so sessions abandoned by their
/// browser never hold storage, taking over the sweep duty the SignaCore client package's own
/// cleanup service used to drive (the framework handler has no sweep hook). One bounded batch
/// per tick; the next interval retries whatever remains.
/// </summary>
public sealed class AdminSessionCleanupService(
    AdminSessionTicketStore store,
    ILogger<AdminSessionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                var removed = store.RemoveExpired(stoppingToken);
                if (removed > 0)
                {
                    logger.LogDebug("Removed {Count} expired administrator session rows", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep never stops the service; the next tick retries it.
                logger.LogWarning(exception, "Administrator session sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
