using System;
using System.Threading.Tasks;

namespace Lexarbor.Domain.Repositories;

public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action);
    Task<int> SaveChangesAsync();
}
