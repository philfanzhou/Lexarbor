using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

/// <summary>
/// The unit and entry-kind narrowing a question's distractor queries can be
/// asked to apply. A null <see cref="UnitId"/> means distractors come from the
/// whole book; a null <see cref="PhraseOnly"/> means the entry kind is not
/// filtered at all.
/// </summary>
/// <param name="unitId">
/// Only candidates assigned to this unit of the book. A word qualifies in the
/// Chinese-to-English direction when any of its meanings in the book holds a
/// position of the unit; a meaning qualifies in the English-to-Chinese
/// direction when it holds a position of the unit itself.
/// </param>
/// <param name="phraseOnly">
/// How the entry-kind filter selects candidates. The kind of a candidate is
/// read from the same scope the unit filter would use — the unit when
/// <see cref="UnitId"/> names one, the whole book otherwise — and a candidate
/// that holds one <c>phrase</c> position there is a phrase. True keeps only
/// phrase candidates, false keeps only word candidates (unclassified ones
/// included), null keeps both.
/// </param>
public sealed record VocabularyDistractorScope(string? UnitId, bool? PhraseOnly);

public interface IVocabularyRepository
{
    Task<Vocabulary?> GetByIdAsync(string id);
    Task<Vocabulary?> GetByIdAsync(string id, CancellationToken cancellationToken) => RepositoryRead.WithCancellation(() => GetByIdAsync(id), cancellationToken);
    Task<Vocabulary?> GetByNormalizedWordAsync(string normalizedWord);
    Task<(List<Vocabulary> Items, int TotalCount)> SearchAsync(string? keyword, int page, int size);
    Task AddAsync(Vocabulary model);
    Task UpdateAsync(Vocabulary model);
    /// <param name="excludeEquivalentMeaning">
    /// The definition the question is asking about. A candidate that carries an
    /// equivalent definition in the same book is a correct answer to that stem,
    /// not a distractor, so it is excluded along with the target word itself.
    /// </param>
    Task<List<Vocabulary>> GetRandomByBookExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeWord,
        string excludeEquivalentMeaning,
        int count,
        VocabularyDistractorScope? scope = null);
    Task<List<Vocabulary>> GetRandomByBookExceptAsync(
        string bookId, string excludeVocabularyId, string excludeWord, string excludeEquivalentMeaning, int count, VocabularyDistractorScope? scope, CancellationToken cancellationToken)
        => RepositoryRead.WithCancellation(() => GetRandomByBookExceptAsync(bookId, excludeVocabularyId, excludeWord, excludeEquivalentMeaning, count, scope), cancellationToken);
}

public interface IVocabularyBookRepository
{
    Task<VocabularyBook?> GetByIdAsync(string id);
    Task<VocabularyBook?> GetByIdAsync(string id, CancellationToken cancellationToken) => RepositoryRead.WithCancellation(() => GetByIdAsync(id), cancellationToken);
    Task<List<VocabularyBook>> GetAllAsync();
    Task<List<VocabularyBook>> GetActiveAsync(bool excludeManagedSupplement = false);
    Task<(List<VocabularyBook> Items, int TotalCount)> SearchAsync(string? keyword, int page, int size);
    Task<List<VocabularyBook>> GetByCategoryAsync(string? category, string? grade);
    Task<List<string>> GetDistinctCategoriesAsync();
    Task<List<string>> GetEducationLevelsAsync();
    Task<List<string>> GetDistinctGradesAsync();
    Task<List<string>> GetGradesByEducationLevelAsync(string educationLevel);
    Task<bool> HasMeaningsAsync(string bookId);
    Task<(List<Vocabulary> Items, int TotalCount)> GetWordsAsync(string bookId, int page, int size);
    Task AddAsync(VocabularyBook model);
    Task UpdateAsync(VocabularyBook model);
    Task DeleteAsync(string id);
}

public interface IVocabularyMeaningRepository
{
    Task<VocabularyMeaning?> GetByIdAsync(string id);
    Task<List<VocabularyMeaning>> GetByVocabularyIdAsync(string vocabularyId);
    Task<List<VocabularyMeaning>> GetByBookIdAsync(string bookId);
    Task<List<VocabularyMeaning>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId);
    Task<List<VocabularyMeaning>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId, CancellationToken cancellationToken)
        => RepositoryRead.WithCancellation(() => GetByBookAndVocabularyIdAsync(bookId, vocabularyId), cancellationToken);
    Task<VocabularyMeaning?> GetEquivalentAsync(
        string vocabularyId,
        string bookId,
        string normalizedPartOfSpeech,
        string meaning);
    Task<List<VocabularyMeaning>> GetRandomDistinctVocabularyExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeMeaning,
        int count,
        VocabularyDistractorScope? scope = null);
    Task<List<VocabularyMeaning>> GetRandomDistinctVocabularyExceptAsync(
        string bookId, string excludeVocabularyId, string excludeMeaning, int count, VocabularyDistractorScope? scope, CancellationToken cancellationToken)
        => RepositoryRead.WithCancellation(() => GetRandomDistinctVocabularyExceptAsync(bookId, excludeVocabularyId, excludeMeaning, count, scope), cancellationToken);
    Task AddAsync(VocabularyMeaning model);
    Task UpdateAsync(VocabularyMeaning model);
    Task DeleteAsync(string id);
    Task DeleteByVocabularyIdAsync(string vocabularyId);
}

public interface IVocabularyBookUnitRepository
{
    Task<VocabularyBookUnit?> GetByIdAsync(string id);
    Task<VocabularyBookUnit?> GetByIdAsync(string id, CancellationToken cancellationToken) => RepositoryRead.WithCancellation(() => GetByIdAsync(id), cancellationToken);
    Task<List<VocabularyBookUnit>> GetByBookIdAsync(string bookId);
    /// <summary>
    /// How many distinct meanings each unit of one book has assigned, from a
    /// single grouped read. A meaning that holds two positions of one unit —
    /// its Section A and its Section B — is one meaning, not two. Units with
    /// no assignments are absent from the dictionary, and the caller treats a
    /// missing key as zero.
    /// </summary>
    Task<Dictionary<string, int>> GetAssignmentCountsAsync(string bookId);
    Task AddAsync(VocabularyBookUnit model);
    Task UpdateAsync(VocabularyBookUnit model);
    Task DeleteAsync(string id);
}

public interface IVocabularyMeaningUnitRepository
{
    /// <param name="section">Null for the unsectioned position of the unit.</param>
    /// <param name="entryKind">Null for the unclassified position of that place.</param>
    Task<bool> ExistsAsync(string unitId, string meaningId, string? section, string? entryKind);
    Task<List<VocabularyMeaningUnit>> GetByUnitIdAsync(string unitId);
    Task<List<VocabularyMeaningUnit>> GetByMeaningIdAsync(string meaningId);
    Task<List<VocabularyMeaningUnit>> GetByMeaningIdAsync(string meaningId, CancellationToken cancellationToken)
        => RepositoryRead.WithCancellation(() => GetByMeaningIdAsync(meaningId), cancellationToken);
    Task AddAsync(VocabularyMeaningUnit model);
    /// <param name="section">Null for the unsectioned position of the unit.</param>
    /// <param name="entryKind">Null for the unclassified position of that place.</param>
    Task DeleteAsync(string unitId, string meaningId, string? section, string? entryKind);
}

internal static class RepositoryRead
{
    internal static async Task<T> WithCancellation<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await read();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
