using System;
using System.Threading.Tasks;

namespace Lexarbor.Domain.Repositories;

public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action);
    Task<T> ExecuteCatalogWriteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);
    Task<T> ExecuteBookWriteAsync<T>(string bookId, Func<Task<T>> action, CancellationToken cancellationToken = default);
    Task<T> ExecuteWordWriteAsync<T>(string? wordId, IReadOnlyCollection<string> normalizedSpellings,
        Func<Task<T>> action, CancellationToken cancellationToken = default);
    Task<T> ExecuteWordSetWriteAsync<T>(IReadOnlyCollection<string> wordIds, Func<Task<T>> action);
    Task<T> ExecuteMeaningWriteAsync<T>(string meaningId, Func<Task<T>> action);
    Task<int> SaveChangesAsync();
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
