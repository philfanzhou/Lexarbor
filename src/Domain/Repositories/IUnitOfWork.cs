using System;
using System.Threading.Tasks;

namespace Ruoyu.Study.Vocabulary.Domain.Repositories;

public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action);
    Task<int> SaveChangesAsync();
}
