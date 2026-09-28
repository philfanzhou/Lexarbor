using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyAdminQueryService(IVocabularyAdminQueryRepository repository)
{
    public Task<VocabularyAdminPage> SearchAsync(string? keyword, string? bookId, int? page, int? size,
        CancellationToken cancellationToken = default)
    {
        var paging = Paging(page, size);
        return repository.SearchAsync(keyword?.Trim(), string.IsNullOrWhiteSpace(bookId) ? null : bookId.Trim(),
            paging.Page, paging.Size, cancellationToken);
    }

    public Task<VocabularyAdminWord> GetAsync(string wordId, CancellationToken cancellationToken = default)
        => repository.GetAsync(wordId, cancellationToken);

    public Task<VocabularyAdminContent> GetContentAsync(string bookId, string? keyword, int? page, int? size,
        CancellationToken cancellationToken = default)
    {
        var paging = Paging(page, size);
        return repository.GetContentAsync(bookId, keyword?.Trim(), paging.Page, paging.Size, cancellationToken);
    }

    public Task<VocabularyAdminUnitContent> GetUnitContentAsync(
        string bookId,
        string unitId,
        string? keyword,
        string? section,
        string? entryKind,
        int? page,
        int? size,
        CancellationToken cancellationToken = default)
    {
        var paging = Paging(page, size);
        return repository.GetUnitContentAsync(
            bookId.Trim(),
            unitId.Trim(),
            keyword?.Trim(),
            ResolveSection(section),
            ResolveEntryKind(entryKind),
            paging.Page,
            paging.Size,
            cancellationToken);
    }

    /// <summary>
    /// Maps the query parameter: absent or blank reads every kind of the
    /// unit's places, <c>word</c> and <c>phrase</c> name a kind, and
    /// <c>none</c> names the unclassified places. Anything else is refused
    /// rather than guessed at.
    /// </summary>
    private static string? ResolveEntryKind(string? entryKind)
    {
        if (string.IsNullOrWhiteSpace(entryKind))
        {
            return null;
        }

        return entryKind.Trim() switch
        {
            "word" => "word",
            "phrase" => "phrase",
            "none" => string.Empty,
            _ => throw new DomainValidationException("EntryKind must be word, phrase, or none.")
        };
    }

    /// <summary>
    /// Maps the query parameter: absent or blank reads every place of the unit,
    /// <c>A</c> and <c>B</c> name a section, and <c>none</c> names the
    /// unsectioned places. Anything else is refused rather than guessed at.
    /// </summary>
    private static string? ResolveSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return null;
        }

        return section.Trim() switch
        {
            "A" => "A",
            "B" => "B",
            "none" => string.Empty,
            _ => throw new DomainValidationException("Section must be A, B, or none.")
        };
    }

    private static (int Page, int Size) Paging(int? requestedPage, int? requestedSize)
    {
        var page = requestedPage is null or 0 ? 1 : requestedPage.Value;
        var size = requestedSize is null or 0 ? 20 : requestedSize.Value;
        if (page < 1 || size < 1 || size > 100 || (long)(page - 1) * size > int.MaxValue)
            throw new DomainValidationException("Paging parameters are invalid.");
        return (page, size);
    }
}
