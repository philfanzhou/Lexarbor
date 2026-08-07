using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private readonly VocabularyDbContext _dbContext;

    public UnitOfWork(VocabularyDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action)
    {
        await WriteLock.WaitAsync();
        try
        {
            await using var transaction =
                await _dbContext.Database.BeginTransactionAsync();
            var result = await action();
            await transaction.CommitAsync();
            return result;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<int> SaveChangesAsync()
    {
        try
        {
            return await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new ConflictException(
                "The requested vocabulary data conflicts with existing data.",
                exception);
        }
    }
}
