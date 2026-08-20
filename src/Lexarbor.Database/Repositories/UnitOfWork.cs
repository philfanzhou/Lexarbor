using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;

namespace Lexarbor.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    /// <summary>SQLITE_BUSY: another connection holds the database.</summary>
    private const int SqliteBusyErrorCode = 5;

    /// <summary>SQLITE_LOCKED: a table in this database is locked.</summary>
    private const int SqliteLockedErrorCode = 6;

    /// <summary>SQLITE_CONSTRAINT: a unique or foreign key constraint failed.</summary>
    private const int SqliteConstraintErrorCode = 19;

    /// <summary>
    /// SQLite admits one writer at a time, and a single instance is the
    /// deployment this service documents, so writes are serialized here rather
    /// than left to collide in the database and be retried by the driver.
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

    private readonly VocabularyDbContext _dbContext;

    public UnitOfWork(VocabularyDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action)
    {
        if (HoldsWriteLock.Value)
        {
            // Already inside a serialized write on this context. Join it: a
            // second BeginTransactionAsync on the same connection is rejected,
            // and the enclosing transaction is the scope that should commit.
            return await action();
        }

        await WriteLock.WaitAsync();
        HoldsWriteLock.Value = true;
        try
        {
            return await TranslateStorageErrorsAsync(async () =>
            {
                await using var transaction =
                    await _dbContext.Database.BeginTransactionAsync();
                var result = await action();
                await transaction.CommitAsync();
                return result;
            });
        }
        finally
        {
            HoldsWriteLock.Value = false;
            WriteLock.Release();
        }
    }

    public async Task<int> SaveChangesAsync()
    {
        // Taking the lock here as well as in ExecuteInTransactionAsync is the
        // point: the book write paths call this directly, so they used to reach
        // SQLite unserialized while the import path was being protected.
        if (HoldsWriteLock.Value)
        {
            return await TranslateStorageErrorsAsync(() => _dbContext.SaveChangesAsync());
        }

        await WriteLock.WaitAsync();
        HoldsWriteLock.Value = true;
        try
        {
            return await TranslateStorageErrorsAsync(() => _dbContext.SaveChangesAsync());
        }
        finally
        {
            HoldsWriteLock.Value = false;
            WriteLock.Release();
        }
    }

    private static async Task<T> TranslateStorageErrorsAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception)
            when (HasSqliteErrorCode(exception, SqliteConstraintErrorCode))
        {
            throw new ConflictException(
                "The requested vocabulary data conflicts with existing data.",
                exception);
        }
        catch (Exception exception)
            when (HasSqliteErrorCode(exception, SqliteBusyErrorCode, SqliteLockedErrorCode))
        {
            // Reported separately from a conflict because it is not about the
            // data: nothing is wrong with the request and retrying it works.
            throw new StorageBusyException(
                "The vocabulary database is busy. Please retry the request.",
                exception);
        }
    }

    /// <summary>
    /// A lock or constraint failure arrives wrapped in a DbUpdateException from
    /// SaveChanges but bare from a commit, so both shapes are checked.
    /// </summary>
    private static bool HasSqliteErrorCode(Exception exception, params int[] errorCodes)
    {
        var sqliteException = exception as SqliteException
                              ?? exception.InnerException as SqliteException;
        return sqliteException != null
               && Array.IndexOf(errorCodes, sqliteException.SqliteErrorCode) >= 0;
    }
}
