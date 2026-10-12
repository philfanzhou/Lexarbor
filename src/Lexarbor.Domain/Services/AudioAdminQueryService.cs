using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class AudioAdminQueryService(IAudioReadRepository repository)
{
    public Task<AudioTargetPage> SearchAsync(string? bookId, string? locale, string? status, int? page, int? size,
        CancellationToken cancellationToken)
    {
        var resolvedPage = page ?? 1;
        var resolvedSize = size ?? 20;
        if (resolvedPage < 1 || resolvedSize < 1 || resolvedSize > 100 || (long)(resolvedPage - 1) * resolvedSize > int.MaxValue)
            throw new DomainValidationException("Paging parameters are invalid.");
        AudioLocale? resolvedLocale = locale switch
        {
            null => null,
            "en-GB" => AudioLocale.EnGb,
            "en-US" => AudioLocale.EnUs,
            _ => throw new DomainValidationException("Audio locale must be en-GB or en-US.")
        };
        AudioContentStatus? resolvedStatus = status switch
        {
            null => null,
            "missing" => AudioContentStatus.Missing,
            "ready" => AudioContentStatus.Ready,
            "stale" => AudioContentStatus.Stale,
            _ => throw new DomainValidationException("Audio status must be missing, ready, or stale.")
        };
        if (bookId != null && string.IsNullOrWhiteSpace(bookId))
            throw new DomainValidationException("Book ID is invalid.");
        return repository.SearchTargetsAsync(new(bookId, resolvedLocale, resolvedStatus, resolvedPage, resolvedSize), cancellationToken);
    }

    public async Task<IReadOnlyList<AudioTarget>> GetAsync(string wordId, string? meaningId, CancellationToken cancellationToken)
    {
        if (meaningId != null && string.IsNullOrWhiteSpace(meaningId))
            throw new DomainValidationException("Meaning ID is invalid.");
        var snapshot = await repository.GetSnapshotAsync(wordId, meaningId, cancellationToken);
        return Enum.GetValues<AudioLocale>().Select(locale => new AudioTarget(wordId, meaningId,
            AudioSelectionService.Select(snapshot, locale))).ToList();
    }
}
