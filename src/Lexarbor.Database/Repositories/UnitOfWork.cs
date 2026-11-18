using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Npgsql;

namespace Lexarbor.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    /// <summary>
    /// Writes are serialized in this process, a guarantee about 66 call sites
    /// rely on. It predates PostgreSQL — SQLite admitted one writer at a time —
    /// and stays until every invariant it protects has a database-level
    /// guarantee of its own (ADR-011, "Write concurrency"). A single serving
    /// instance remains the supported deployment while it exists.
    /// </summary>
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    /// <summary>
    /// Marks an async flow that already holds <see cref="WriteLock"/>. The
    /// semaphore is not reentrant, so without this a SaveChangesAsync nested
    /// inside an ExecuteInTransactionAsync would wait on a lock its own caller
    /// is holding and never wake. It is assigned only in the two methods that
    /// take the lock: an AsyncLocal written inside a helper is not visible to
    /// the caller that has to clear it again.
    /// </summary>
    private static readonly AsyncLocal<bool> HoldsWriteLock = new();
    internal static bool IsWriteInProgress => HoldsWriteLock.Value;
    private static readonly AsyncLocal<VocabularyDbContext?> WriteContext = new();
    internal static bool OwnsWriteContext(VocabularyDbContext context) => HoldsWriteLock.Value && ReferenceEquals(WriteContext.Value, context);

    private readonly VocabularyDbContext _dbContext;

    public UnitOfWork(VocabularyDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action) =>
        ExecuteInTransactionAsync(action, CancellationToken.None);

    public Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) =>
        ExecuteTransactionAsync(action, cancellationToken, false);

    // Audio operations honour cancellation while queued, then finish the short
    // database commit just like existing vocabulary services once admitted.
    internal Task<T> ExecuteAdmittedTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) =>
        ExecuteTransactionAsync(action, cancellationToken, true);

    private async Task<T> ExecuteTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken, bool finishAfterAdmission)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HoldsWriteLock.Value)
        {
            // Already inside a serialized write on this context. Join it: a
            // second BeginTransactionAsync on the same connection is rejected,
            // and the enclosing transaction is the scope that should commit.
            return await action();
        }

        await WriteLock.WaitAsync(cancellationToken);
        if (finishAfterAdmission) cancellationToken = CancellationToken.None;
        HoldsWriteLock.Value = true;
        WriteContext.Value = _dbContext;
        var committed = false;
        try
        {
            return await TranslateStorageErrorsAsync(async () =>
            {
                await using var transaction =
                    await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var result = await action();
                cancellationToken.ThrowIfCancellationRequested();
                // Once commit starts its outcome may be unknown to a disconnected caller.
                await transaction.CommitAsync(cancellationToken);
                committed = true;
                return result;
            });
        }
        finally
        {
            WriteContext.Value = null;
            HoldsWriteLock.Value = false;
            WriteLock.Release();
            _dbContext.FinishEnrichmentChanges(committed);
        }
    }

    public Task<int> SaveChangesAsync() => SaveChangesAsync(CancellationToken.None);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Taking the lock here as well as in ExecuteInTransactionAsync is the
        // point: the book write paths call this directly, so they used to reach
        // the database unserialized while the import path was being protected.
        if (HoldsWriteLock.Value)
        {
            return await TranslateStorageErrorsAsync(() => _dbContext.SaveChangesAsync(cancellationToken));
        }

        await WriteLock.WaitAsync(cancellationToken);
        HoldsWriteLock.Value = true;
        WriteContext.Value = _dbContext;
        var committed = false;
        try
        {
            var count = await TranslateStorageErrorsAsync(() => _dbContext.SaveChangesAsync(cancellationToken));
            committed = true;
            return count;
        }
        finally
        {
            WriteContext.Value = null;
            HoldsWriteLock.Value = false;
            WriteLock.Release();
            _dbContext.FinishEnrichmentChanges(committed);
        }
    }

    private static async Task<T> TranslateStorageErrorsAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (StorageErrors.IsConstraintViolation(exception))
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
    /// check, not-null, and exclusion constraints. SQLite reported all of them
    /// as SQLITE_CONSTRAINT.
    /// </summary>
    internal static bool IsConstraintViolation(Exception exception) =>
        FindPostgresException(exception)?.SqlState is { } state
        && state.StartsWith("23", StringComparison.Ordinal);

    /// <summary>
    /// A lock wait that ran past <c>lock_timeout</c> (55P03), a deadlock
    /// (40P01), or a serialization failure (40001): the PostgreSQL shapes of
    /// SQLite's SQLITE_BUSY and SQLITE_LOCKED. Retrying the request works.
    /// </summary>
    internal static bool IsBusy(Exception exception) =>
        FindPostgresException(exception)?.SqlState is PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure;

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
