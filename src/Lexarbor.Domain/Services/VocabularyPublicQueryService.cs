using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

/// <summary>
/// The application service behind catalog browsing and vocabulary family lookup. It
/// trims caller input the same way the administrative query service does;
/// paging itself is already normalized by the endpoint that calls in.
/// </summary>
public sealed class VocabularyPublicQueryService(IVocabularyPublicQueryRepository repository)
{
    public Task<IReadOnlyList<VocabularyFamilyLookup>> LookupRelationsAsync(IReadOnlyList<VocabularyFamilyQuery> queries, CancellationToken cancellationToken)
        => repository.LookupRelationsAsync(queries, cancellationToken);

    public Task<VocabularyPublicUnitList> GetUnitsAsync(string bookId, CancellationToken cancellationToken = default)
        => repository.GetUnitsAsync(bookId.Trim(), cancellationToken);

    public Task<VocabularyPublicEntryPage> GetEntriesAsync(
        string bookId, string? unitId, int page, int size, CancellationToken cancellationToken = default)
        => repository.GetEntriesAsync(
            bookId.Trim(),
            string.IsNullOrWhiteSpace(unitId) ? null : unitId.Trim(),
            page,
            size,
            cancellationToken);
}
