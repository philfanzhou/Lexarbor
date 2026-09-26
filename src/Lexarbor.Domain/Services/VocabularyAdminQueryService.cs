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

    private static (int Page, int Size) Paging(int? requestedPage, int? requestedSize)
    {
        var page = requestedPage is null or 0 ? 1 : requestedPage.Value;
        var size = requestedSize is null or 0 ? 20 : requestedSize.Value;
        if (page < 1 || size < 1 || size > 100 || (long)(page - 1) * size > int.MaxValue)
            throw new DomainValidationException("Paging parameters are invalid.");
        return (page, size);
    }
}
