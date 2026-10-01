using System.Data.Common;
using Lexarbor.Database;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Health;
using ServiceMantle.Installation;

namespace Lexarbor.Host;

/// <summary>
/// The outcome of this process's startup database initialization, recorded once
/// and projected into every readiness snapshot. Lexarbor has no installation
/// phases, so the phase is always <see cref="ServiceStartupPhase.Completed"/>
/// and the migration state is the only startup input readiness has.
/// </summary>
public sealed class LexarborHealthState
{
    private volatile ServiceMigrationReadinessState _migrationStatus = ServiceMigrationReadinessState.NotStarted;

    public ServiceMigrationReadinessState MigrationStatus
    {
        get => _migrationStatus;
        set => _migrationStatus = value;
    }
}

/// <summary>
/// Readiness snapshots for the ServiceMantle health endpoints. The database
/// status comes from one bounded read-only SQLite probe per request: opening
/// the configured connection and evaluating <c>SELECT 1</c>. No path, version,
/// connection string, or exception text is ever projected.
/// </summary>
public sealed class LexarborHealthSnapshotSource(
    LexarborHealthState state,
    IServiceScopeFactory scopeFactory) : IServiceHealthSnapshotSource
{
    public const string DatabaseUnreachableErrorCode = "vocabulary.database_unreachable";

    /// <summary>
    /// Bounds the probe independent of the request, so a wedged SQLite call
    /// answers "not ready" instead of hanging the health endpoint. The
    /// ServiceMantle probe timeout bounds the same call from the caller side.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var reachable = await ProbeDatabaseAsync(cancellationToken);
        return new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            state.MigrationStatus,
            reachable ? ServiceDatabaseReadinessState.Reachable : ServiceDatabaseReadinessState.Unreachable,
            reachable ? null : DatabaseUnreachableErrorCode);
    }

    private async Task<bool> ProbeDatabaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            // One read-only scalar query through EF's own connection handling:
            // bound by the probe timeout, pooled for file databases, and left
            // untouched for a connection the host already holds open.
            var result = await context.Database
                .SqlQueryRaw<long>("SELECT 1 AS Value")
                .SingleAsync(timeout.Token);
            return result == 1;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or DbException or InvalidOperationException or IOException)
        {
            // Any probe failure — including the timeout's cancellation — is one
            // fixed unreachable answer. The cause stays out of the snapshot.
            return false;
        }
    }
}
