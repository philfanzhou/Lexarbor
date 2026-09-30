using System;
using System.Threading.Tasks;

namespace Lexarbor.Domain.Repositories;

public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action);
    Task<int> SaveChangesAsync();
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
