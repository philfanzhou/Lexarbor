using Lexarbor.Learning.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Learning.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly LearningDbContext _dbContext;

    public UnitOfWork(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void DiscardChanges() => _dbContext.ChangeTracker.Clear();

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try { return await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch { DiscardChanges(); throw; }
    }

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var transaction = _dbContext.Database.IsRelational()
                ? await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var result = await work().ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            DiscardChanges();
            throw;
        }
    }
}
