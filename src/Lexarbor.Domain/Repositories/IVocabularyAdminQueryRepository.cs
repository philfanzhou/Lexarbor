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
    /// <param name="entryKind">
    /// Null reads every kind of those places; otherwise the stored entry kind —
    /// the empty string for the unclassified places — narrows the page and the
    /// counts to that kind's places. It combines with <paramref name="section"/>
    /// as two independent dimensions of one position.
    /// </param>
    Task<VocabularyAdminUnitContent> GetUnitContentAsync(string bookId, string unitId, string? keyword, string? section, string? entryKind, int page, int size, CancellationToken cancellationToken);
}
