using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Migration;

namespace Lexarbor.Database;

/// <summary>
/// Lexarbor's execution boundary for the ServiceMantle migration orchestration:
/// the inspection reads only the EF migration history, and the execution is one
/// EF migration run plus the write-ahead-logging switch.
/// </summary>
/// <remarks>
/// <para>
/// The inspection never creates, writes, or repairs anything. It proves the
/// finite states it can read from the <c>__EFMigrationsHistory</c> table; a
/// database whose history cannot be read — the table is absent, which is the
/// normal state of a freshly published empty file — is empty, and any other
/// read failure stays an exception for the orchestrator to classify as
/// <see cref="MigrationObservationState.InspectionFailed"/>. A history row this
/// build does not know is a newer schema and is refused rather than adopted.
/// </para>
/// <para>
/// Execution runs <c>MigrateAsync</c> on the application's own context and then
/// switches the database to write-ahead logging, under which a reader no longer
/// blocks a writer. The WAL setting lives in the database header rather than the
/// connection, so it is a no-op after the first start; it runs on every start so
/// that a database restored from a backup taken elsewhere is switched over too.
/// </para>
/// </remarks>
public sealed class LexarborMigrationExecutor : IDatabaseMigrationExecutor
{
    private const string HistoryTable = "__EFMigrationsHistory";

    private readonly VocabularyDbContext context;

    public LexarborMigrationExecutor(VocabularyDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
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
        List<string> applied;
        try
        {
            applied = await ReadAppliedMigrationsAsync(cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 1
                && exception.Message.Contains("no such table", StringComparison.Ordinal))
        {
            // No history table at all: an empty database — the startup gate just
            // published it, or an operator created an empty file — is adoptable.
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

        return applied.Count < known.Length
            ? MigrationObservationState.PendingMigration
            : MigrationObservationState.CurrentVersionCompatible;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is read once more after the migration and the WAL
    /// switch return, so a cancellation requested by that checkpoint is reported
    /// with the caller's own token instead of a completed execution. A migration
    /// that fails keeps its own exception unchanged for the orchestrator.
    /// </remarks>
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.MigrateAsync(cancellationToken);
        await EnableWriteAheadLoggingAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<List<string>> ReadAppliedMigrationsAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = context.Database.GetDbConnection();
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

    private async Task EnableWriteAheadLoggingAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            // PRAGMA cannot be composed into a query, so it goes through the
            // raw command rather than through SqlQuery.
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
