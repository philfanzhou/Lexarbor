using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

/// <summary>
/// The catalog reads behind book browsing and vocabulary family lookup. Book browsing sees
/// only enabled books: a missing book answers the detail route's 404 and a
/// disabled one its 422, so no unit or entry of a disabled book is ever
/// listed.
/// </summary>
public interface IVocabularyPublicQueryRepository
{
    Task<IReadOnlyList<VocabularyFamilyLookup>> LookupRelationsAsync(IReadOnlyList<VocabularyFamilyQuery> queries, CancellationToken cancellationToken);

    Task<LearningQuestionRelationContext?> GetLearningRelationAsync(string wordId, LearningQuestionRelation relation, CancellationToken cancellationToken);

    Task<IReadOnlyList<VocabularyQuestionOptionModel>?> GetReviewDistractorsAsync(VocabularyModel word, VocabularyMeaningModel meaning,
        VocabularyDistractorScope? scope, bool chineseToEnglish, bool supplementary, CancellationToken cancellationToken);

    Task<PartOfSpeechChoiceCandidates?> GetPartOfSpeechChoiceAsync(VocabularyModel word, VocabularyMeaningModel meaning,
        VocabularyPartOfSpeech partOfSpeech, VocabularyDistractorScope? scope, bool supplementary, CancellationToken cancellationToken);

    Task<VocabularyPublicUnitList> GetUnitsAsync(string bookId, CancellationToken cancellationToken);

    /// <param name="unitId">
    /// Null reads the whole book; otherwise only meanings assigned to that
    /// unit, whose places inside it are the entries' only positions. A unit
    /// of another book answers the same 404 as a missing one.
    /// </param>
    Task<VocabularyPublicEntryPage> GetEntriesAsync(
        string bookId, string? unitId, int page, int size, CancellationToken cancellationToken);
}
