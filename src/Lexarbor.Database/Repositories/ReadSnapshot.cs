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
