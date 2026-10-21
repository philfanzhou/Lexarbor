using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyCleanupRepository
{
    Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken);
    Task<VocabularyBookModel?> GetBookAsync(string bookId, CancellationToken cancellationToken);
    Task<bool> WordExistsAsync(string wordId, CancellationToken cancellationToken);
    Task<VocabularyMeaningModel?> GetMeaningAsync(string meaningId, CancellationToken cancellationToken);
    Task<int> CountSelectedWordsAsync(string bookId, IReadOnlyList<string> wordIds, CancellationToken cancellationToken);
    Task<VocabularyCleanupCounts> CountAsync(string bookId, VocabularyCleanupSelection selection, CancellationToken cancellationToken);
    Task<VocabularyCleanupResult> DeleteAsync(string bookId, VocabularyCleanupSelection selection);
}
