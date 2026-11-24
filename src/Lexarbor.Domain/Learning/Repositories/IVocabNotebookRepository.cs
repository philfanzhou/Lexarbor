using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabNotebookRepository
{
    /// <summary>All items of a student, oldest first.</summary>
    Task<List<VocabNotebookItemModel>> ListAsync(Guid studentId, CancellationToken cancellationToken = default);

    Task<List<string>> PendingSubmissionIdsAsync(Guid studentId, CancellationToken cancellationToken);

    Task<List<VocabNotebookItemModel>> ReviewedPendingAsync(Guid studentId, string[] submissionIds, CancellationToken cancellationToken);

    Task<VocabNotebookSummary> SummaryAsync(Guid studentId, DateTimeOffset recentStart, CancellationToken cancellationToken);

    Task<VocabNotebookPage<VocabNotebookItemModel>> QueryAsync(Guid studentId, VocabNotebookQuery filters,
        DateTimeOffset recentStart, CancellationToken cancellationToken);

    Task<List<CardKey>> MembershipAsync(Guid studentId, IReadOnlyCollection<CardKey> keys, CancellationToken cancellationToken);

    Task<VocabNotebookItemModel?> FindAsync(Guid studentId, Guid itemId, CancellationToken cancellationToken = default);

    Task<VocabNotebookItemModel?> FindByKeyAsync(Guid studentId, CardKey key, CancellationToken cancellationToken = default);

    Task AddAsync(VocabNotebookItemModel item, CancellationToken cancellationToken = default);

    Task UpdateAsync(VocabNotebookItemModel item, CancellationToken cancellationToken = default);

    Task RemoveAsync(VocabNotebookItemModel item, CancellationToken cancellationToken = default);

    /// <summary>Serialize writes for this student in the current transaction or a new one.</summary>
    Task<T> InStudentWriteAsync<T>(Guid studentId, Func<Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a read-then-write in one REPEATABLE READ transaction. When a row it read is changed or removed
    /// concurrently before its write, or its insert takes a key that was added concurrently, the save throws
    /// VocabNotebookWriteConflictException and nothing is committed.
    /// </summary>
    Task<T> InRepeatableReadAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default);
}
