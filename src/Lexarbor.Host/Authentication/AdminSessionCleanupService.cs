using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;

namespace Lexarbor.Host.Authentication;

public sealed class AdminSessionCleanupService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider,
    ILogger<AdminSessionCleanupService> logger) : BackgroundService
{
    public const string FailureDiagnostic = "Expired administrator session cleanup failed; the next batch will retry.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        try
        {
            // Cleanup is not a startup/readiness prerequisite, including when initialization is disabled.
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunBatchAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CleanupAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is AdminSessionStorageException or StorageBusyException)
        {
            logger.LogWarning(FailureDiagnostic);
        }
    }
}
