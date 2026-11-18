using Lexarbor.Database.LegacySqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Migration;

namespace Lexarbor.Database;

/// <summary>
/// Lexarbor's execution boundary for the ServiceMantle migration orchestration:
/// the inspection reads only the EF migration history, and the execution is one
/// EF migration run.
/// </summary>
/// <remarks>
/// <para>
/// The inspection never creates, writes, or repairs anything. It proves the
/// finite states it can read from the <c>__EFMigrationsHistory</c> table; a
/// database whose history cannot be read — the table is absent, which is the
/// normal state of a freshly created database — is empty, and any other
/// read failure stays an exception for the orchestrator to classify as
/// <see cref="MigrationObservationState.InspectionFailed"/>. A history row this
/// build does not know is a newer schema and is refused rather than adopted.
/// </para>
/// <para>
/// Execution runs <c>MigrateAsync</c> on the application's own context and
/// then the transition release's legacy SQLite step
/// (<see cref="LegacySqliteImporter"/>), under the startup gate's advisory
/// migration lock — see <see cref="LexarborDatabaseStartup"/>. A schema whose
/// history is current but whose import decision is not yet recorded inspects
/// as pending, so that step runs exactly once per database and is retried
/// after a failed copy.
/// </para>
/// </remarks>
public sealed class LexarborMigrationExecutor : IDatabaseMigrationExecutor
{
    private const string HistoryTable = "__EFMigrationsHistory";

    private readonly VocabularyDbContext context;
    private readonly LegacySqliteImporter legacyImport;

    public LexarborMigrationExecutor(VocabularyDbContext context, LegacySqliteImporter legacyImport)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(legacyImport);
        this.context = context;
        this.legacyImport = legacyImport;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is observed before the read, and the finite states are
    /// decided only from what the history table actually answered.
    /// </remarks>
    public async ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var known = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        // No history table at all: an empty database — the startup gate just
        // created it, or an operator created an empty one — is adoptable. The
        // table is looked up rather than read and caught, so a first start does
        // not log a failed command.
        var applied = await ReadAppliedMigrationsAsync(cancellationToken);
        if (applied is null)
        {
            return MigrationObservationState.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (applied.Any(id => !known.Contains(id, StringComparer.Ordinal)))
        {
            // A record this build does not know is treated as a newer schema
            // rather than guessed at: the orchestration refuses to proceed.
            return MigrationObservationState.VersionTooNew;
        }

        if (applied.Count == 0)
        {
            return MigrationObservationState.Empty;
        }

        if (applied.Count < known.Length)
        {
            return MigrationObservationState.PendingMigration;
        }

        return await legacyImport.RequiresExecutionAsync(cancellationToken)
            ? MigrationObservationState.PendingMigration
            : MigrationObservationState.CurrentVersionCompatible;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is read once more after the migration returns, so a
    /// cancellation requested by that checkpoint is reported with the caller's
    /// own token instead of a completed execution. A migration that fails
    /// keeps its own exception unchanged for the orchestrator.
    /// </remarks>
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.MigrateAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await legacyImport.RunAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<List<string>?> ReadAppliedMigrationsAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = context.Database.GetDbConnection();
            await using (var exists = connection.CreateCommand())
            {
                exists.CommandText = $"SELECT to_regclass('\"{HistoryTable}\"') IS NOT NULL";
                if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    return null;
                }
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"MigrationId\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\"";
            var applied = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetString(0));
            }

            return applied;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
