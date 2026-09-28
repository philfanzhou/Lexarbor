using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyAdminQueryRepository
{
    Task<VocabularyAdminPage> SearchAsync(string? keyword, string? bookId, int page, int size, CancellationToken cancellationToken);
    Task<VocabularyAdminWord> GetAsync(string wordId, CancellationToken cancellationToken);
    Task<VocabularyAdminContent> GetContentAsync(string bookId, string? keyword, int page, int size, CancellationToken cancellationToken);
    /// <param name="section">
    /// Null reads every place of the unit; otherwise the stored section — the
    /// empty string for the unsectioned places — narrows the page and the
    /// counts to that section's places.
    /// </param>
    Task<VocabularyAdminUnitContent> GetUnitContentAsync(string bookId, string unitId, string? keyword, string? section, int page, int size, CancellationToken cancellationToken);
}
