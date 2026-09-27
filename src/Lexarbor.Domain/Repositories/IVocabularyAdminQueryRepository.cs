using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyAdminQueryRepository
{
    Task<VocabularyAdminPage> SearchAsync(string? keyword, string? bookId, int page, int size, CancellationToken cancellationToken);
    Task<VocabularyAdminWord> GetAsync(string wordId, CancellationToken cancellationToken);
    Task<VocabularyAdminContent> GetContentAsync(string bookId, string? keyword, int page, int size, CancellationToken cancellationToken);
}
