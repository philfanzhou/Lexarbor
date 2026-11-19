using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabNotebookRepository
{
    /// <summary>All items of a student, oldest first.</summary>
    Task<List<VocabNotebookItemModel>> ListAsync(Guid studentId, CancellationToken cancellationToken = default);

    Task<VocabNotebookItemModel?> FindAsync(Guid studentId, Guid itemId, CancellationToken cancellationToken = default);

    Task<VocabNotebookItemModel?> FindByKeyAsync(Guid studentId, CardKey key, CancellationToken cancellationToken = default);

    Task AddAsync(VocabNotebookItemModel item, CancellationToken cancellationToken = default);

    Task UpdateAsync(VocabNotebookItemModel item, CancellationToken cancellationToken = default);

    Task RemoveAsync(VocabNotebookItemModel item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a read-then-write in one REPEATABLE READ transaction. When a row it read is changed or removed
    /// concurrently before its write, or its insert takes a key that was added concurrently, the save throws
    /// VocabNotebookWriteConflictException and nothing is committed.
    /// </summary>
    Task<T> InFamilyWriteAsync<T>(Guid studentId, Func<Task<T>> work, CancellationToken cancellationToken = default);

    Task<T> InRepeatableReadAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default);
}
