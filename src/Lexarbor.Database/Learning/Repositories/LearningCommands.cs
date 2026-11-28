using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lexarbor.Learning.Database.Repositories;

/// <summary>The raw-SQL reads the learner repositories share.</summary>
internal static class LearningCommands
{
    /// <summary>
    /// Executes raw SQL with positional parameters through EF's relational pipeline, so the
    /// command is logged and instrumented like any other.
    /// </summary>
    internal static Task<RelationalDataReader> ExecuteReaderAsync(LearningDbContext context, string sql, object[] parameters, CancellationToken cancellationToken)
    {
        var command = context.GetService<IRawSqlCommandBuilder>().Build(sql, parameters);
        return command.RelationalCommand.ExecuteReaderAsync(new RelationalCommandParameterObject(
            context.GetService<IRelationalConnection>(), command.ParameterValues, null, context,
            context.GetService<IRelationalCommandDiagnosticsLogger>()), cancellationToken);
    }

    /// <summary>
    /// Opens the read-only snapshot transaction the learner reads share and proves the learner
    /// exists. <paramref name="create"/> builds the caller's snapshot from the open transaction, so
    /// nothing is handed out before the read-only rule is in force; if anything in here fails,
    /// the transaction is disposed and the failure propagates.
    /// </summary>
    internal static async Task<T> OpenSnapshotAsync<T>(LearningDbContext context, Guid studentId,
        Func<LearningDbContext, IDbContextTransaction, Guid, bool, T> create, CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
            await using var rows = await ExecuteReaderAsync(context, "SELECT id FROM learner_identity WHERE id = {0}", [studentId], cancellationToken).ConfigureAwait(false);
            var exists = await rows.ReadAsync(cancellationToken).ConfigureAwait(false);
            return create(context, transaction, studentId, exists);
        }
        catch { await transaction.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
