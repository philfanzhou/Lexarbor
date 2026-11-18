using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Npgsql;

namespace Lexarbor.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    // Ownership follows the async call chain. Independent requests have independent transactions.
    private static readonly AsyncLocal<VocabularyDbContext?> WriteContext = new();
    internal static bool IsWriteInProgress => WriteContext.Value is not null;
    internal static bool OwnsWriteContext(VocabularyDbContext context) => ReferenceEquals(WriteContext.Value, context);

    private readonly VocabularyDbContext _dbContext;

    public UnitOfWork(VocabularyDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action) =>
        ExecuteInTransactionAsync(action, CancellationToken.None);

    public Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) =>
        ExecuteTransactionAsync(action, cancellationToken, false);

    public Task<T> ExecuteCatalogWriteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default) =>
        ExecuteTransactionAsync(action, cancellationToken, false, token => WriteLocks.CatalogAsync(_dbContext, token));

    public Task<T> ExecuteBookWriteAsync<T>(string bookId, Func<Task<T>> action, CancellationToken cancellationToken = default) =>
        ExecuteTransactionAsync(action, cancellationToken, false, token => WriteLocks.BookAsync(_dbContext, bookId, token));

    public Task<T> ExecuteWordWriteAsync<T>(string? wordId, IReadOnlyCollection<string> normalizedSpellings,
        Func<Task<T>> action, CancellationToken cancellationToken = default) =>
        ExecuteTransactionAsync(action, cancellationToken, false,
            token => WriteLocks.WordsAsync(_dbContext, wordId, normalizedSpellings, token));

    public Task<T> ExecuteWordSetWriteAsync<T>(IReadOnlyCollection<string> wordIds, Func<Task<T>> action) =>
        ExecuteTransactionAsync(action, CancellationToken.None, false, token => WriteLocks.WordSetAsync(_dbContext, wordIds, token));

    public Task<T> ExecuteMeaningWriteAsync<T>(string meaningId, Func<Task<T>> action) =>
        ExecuteTransactionAsync(action, CancellationToken.None, false, token => WriteLocks.MeaningAsync(_dbContext, meaningId, token));

    internal Task<T> ExecuteAdmittedTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? acquire = null) =>
        ExecuteTransactionAsync(action, cancellationToken, true, acquire);

    private async Task<T> ExecuteTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken,
        bool finishAfterAdmission, Func<CancellationToken, Task>? acquire = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsWriteInProgress)
        {
            if (!OwnsWriteContext(_dbContext))
                throw new InvalidOperationException("A nested write must use the owning database context.");
            if (acquire is not null) await acquire(cancellationToken);
            return await action();
        }

        await _dbContext.TransactionSlot.WaitAsync(cancellationToken);
        var committed = false;
        WriteContext.Value = _dbContext;
        try
        {
            return await TranslateStorageErrorsAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                if (acquire is not null) await acquire(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var commitToken = finishAfterAdmission ? CancellationToken.None : cancellationToken;
                var result = await action();
                commitToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(commitToken);
                committed = true;
                return result;
            });
        }
        finally
        {
            WriteContext.Value = null;
            try { _dbContext.FinishEnrichmentChanges(committed); }
            finally { _dbContext.TransactionSlot.Release(); }
        }
    }

    public Task<int> SaveChangesAsync() => SaveChangesAsync(CancellationToken.None);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        OwnsWriteContext(_dbContext)
            ? TranslateStorageErrorsAsync(() => _dbContext.SaveChangesAsync(cancellationToken))
            : ExecuteInTransactionAsync(() => _dbContext.SaveChangesAsync(cancellationToken), cancellationToken);

    private static async Task<T> TranslateStorageErrorsAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (StorageErrors.IsConstraintViolation(exception) || StorageErrors.IsSerializationFailure(exception))
        {
            throw new ConflictException(
                "The requested vocabulary data conflicts with existing data.",
                exception);
        }
        catch (Exception exception) when (StorageErrors.IsBusy(exception))
        {
            // Reported separately from a conflict because it is not about the
            // data: nothing is wrong with the request and retrying it works.
            throw new StorageBusyException(
                "The vocabulary database is busy. Please retry the request.",
                exception);
        }
    }
}

/// <summary>
/// Classifies PostgreSQL failures into the two storage answers the
/// application gives: a data conflict and a retryable busy database.
/// </summary>
internal static class StorageErrors
{
    /// <summary>
    /// SQLSTATE class 23, integrity constraint violation: unique, foreign key,
    /// check, not-null, and exclusion constraints.
    /// </summary>
    internal static bool IsConstraintViolation(Exception exception) =>
        FindPostgresException(exception)?.SqlState is { } state
        && state.StartsWith("23", StringComparison.Ordinal);

    internal static bool IsSerializationFailure(Exception exception) =>
        FindPostgresException(exception)?.SqlState == PostgresErrorCodes.SerializationFailure;

    /// <summary>
    /// A lock wait that ran past <c>lock_timeout</c> (55P03) or a deadlock
    /// (40P01) is a retryable busy database.
    /// </summary>
    internal static bool IsBusy(Exception exception) =>
        FindPostgresException(exception)?.SqlState is PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.DeadlockDetected;

    /// <summary>
    /// A failure arrives bare from a commit or a raw command, wrapped in a
    /// DbUpdateException from SaveChanges, and — when Npgsql classifies it as
    /// transient, as it does a lock timeout or a deadlock — wrapped once more
    /// by the execution strategy, so the whole chain is searched.
    /// </summary>
    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
