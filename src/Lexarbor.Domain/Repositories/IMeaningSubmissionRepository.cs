using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IMeaningSubmissionRepository
{
    Task<MeaningSubmissionModel?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<List<MeaningSubmissionModel>> GetByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest submission with the same normalized word, part of speech and
    /// definition that is still pending, or approved without a changed
    /// definition. Rejected submissions never match, so a rejected definition can
    /// be proposed again.
    /// </summary>
    Task<MeaningSubmissionModel?> FindReusableAsync(
        string normalizedWord,
        string normalizedPartOfSpeech,
        string meaning,
        CancellationToken cancellationToken = default);

    Task<(List<MeaningSubmissionModel> Items, int TotalCount)> PageAsync(
        MeaningSubmissionStatus status,
        int page,
        int size,
        CancellationToken cancellationToken = default);

    Task AddAsync(MeaningSubmissionModel submission, CancellationToken cancellationToken = default);

    Task UpdateAsync(MeaningSubmissionModel submission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every meaning in an enabled book of the first <paramref name="wordLimit"/>
    /// words spelled exactly like, or starting with, the normalized keyword; the
    /// exact spelling first, then shorter words.
    /// </summary>
    Task<List<CatalogMeaningMatch>> SearchCatalogAsync(
        string normalizedKeyword,
        int wordLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="count"/> distinct words from any enabled book, none
    /// spelled like <paramref name="excludeNormalizedWord"/> and none carrying the
    /// definition <paramref name="excludeNormalizedMeaning"/>.
    /// </summary>
    Task<List<VocabularyModel>> GetCatalogWordDistractorsAsync(
        string excludeNormalizedWord,
        string excludeNormalizedMeaning,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="count"/> definitions from any enabled book, one per
    /// word, none of the excluded word and none equal to the excluded definition.
    /// </summary>
    Task<List<VocabularyMeaningModel>> GetCatalogMeaningDistractorsAsync(
        string excludeNormalizedWord,
        string excludeNormalizedMeaning,
        int count,
        CancellationToken cancellationToken = default);
}

/// <summary>An optional assistant that reviews submissions for administrators.</summary>
public interface IMeaningSubmissionReviewAssistant
{
    bool IsEnabled { get; }

    Task<MeaningSubmissionReviewAdvice> ReviewAsync(
        MeaningSubmissionModel submission,
        CancellationToken cancellationToken = default);
}
