using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lexarbor.Database.Repositories;

/// <summary>
/// Opens the read-only snapshot transaction that multi-statement reads share,
/// so a count and the page it describes, or the phases of one scan, observe
/// one instant of the database.
/// </summary>
/// <remarks>
/// <c>REPEATABLE READ</c> takes its snapshot at the first statement and keeps
/// it for the whole transaction. <c>READ ONLY</c> makes the read's intent a
/// database rule: a write attempted inside it fails instead of committing
/// outside the transaction. Concurrent data changes remain invisible to this snapshot.
/// </remarks>
internal static class ReadSnapshot
{
    /// <summary>
    /// Runs <paramref name="read"/> inside one snapshot on an explicitly opened connection,
    /// which is closed again whether the read succeeds or fails.
    /// </summary>
    /// <remarks>
    /// The transaction is a plain <c>BEGIN</c>, not the unit of work's serialized
    /// <c>BEGIN IMMEDIATE</c>: a read must neither queue behind writers nor hold the
    /// write slot.
    /// </remarks>
    internal static async Task<T> ReadAsync<T>(VocabularyDbContext context, Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = await BeginAsync(context, cancellationToken);
            var result = await read();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    internal static async Task<IDbContextTransaction> BeginAsync(
        VocabularyDbContext context,
        CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
