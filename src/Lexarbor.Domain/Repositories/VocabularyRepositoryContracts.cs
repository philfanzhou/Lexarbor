using System.Collections.Generic;
using System.Threading.Tasks;
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
    Task<VocabularyModel?> GetByIdAsync(string id);
    Task<VocabularyModel?> GetByWordAsync(string word);
    Task<VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord);
    Task<List<VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids);
    Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(string? keyword, int page, int size);
    Task AddAsync(VocabularyModel model);
    Task UpdateAsync(VocabularyModel model);
    /// <param name="excludeEquivalentMeaning">
    /// The definition the question is asking about. A candidate that carries an
    /// equivalent definition in the same book is a correct answer to that stem,
    /// not a distractor, so it is excluded along with the target word itself.
    /// </param>
    Task<List<VocabularyModel>> GetRandomByBookExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeWord,
        string excludeEquivalentMeaning,
        int count,
        VocabularyDistractorScope? scope = null);
}

public interface IVocabularyBookRepository
{
    Task<VocabularyBookModel?> GetByIdAsync(string id);
    Task<List<VocabularyBookModel>> GetAllAsync();
    Task<List<VocabularyBookModel>> GetActiveAsync();
    Task<(List<VocabularyBookModel> Items, int TotalCount)> SearchAsync(string? keyword, int page, int size);
    Task<List<VocabularyBookModel>> GetByCategoryAsync(string? category, string? grade);
    Task<List<string>> GetDistinctCategoriesAsync();
    Task<List<string>> GetDistinctEducationLevelsAsync();
    Task<List<string>> GetDistinctGradesAsync();
    Task<List<string>> GetDistinctGradesByEducationLevelAsync(string educationLevel);
    Task<bool> HasMeaningsAsync(string bookId);
    Task<(List<VocabularyModel> Items, int TotalCount)> GetWordsAsync(string bookId, int page, int size);
    Task AddAsync(VocabularyBookModel model);
    Task UpdateAsync(VocabularyBookModel model);
    Task DeleteAsync(string id);
}

public interface IVocabularyMeaningRepository
{
    Task<VocabularyMeaningModel?> GetByIdAsync(string id);
    Task<List<VocabularyMeaningModel>> GetByVocabularyIdAsync(string vocabularyId);
    Task<List<VocabularyMeaningModel>> GetByBookIdAsync(string bookId);
    Task<List<VocabularyMeaningModel>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId);
    Task<VocabularyMeaningModel?> GetEquivalentAsync(
        string vocabularyId,
        string bookId,
        string normalizedPartOfSpeech,
        string meaning);
    Task<List<VocabularyMeaningModel>> GetRandomDistinctVocabularyExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeMeaning,
        int count,
        VocabularyDistractorScope? scope = null);
    Task AddAsync(VocabularyMeaningModel model);
    Task UpdateAsync(VocabularyMeaningModel model);
    Task DeleteAsync(string id);
    Task DeleteByVocabularyIdAsync(string vocabularyId);
}

public interface IVocabularyBookUnitRepository
{
    Task<VocabularyBookUnitModel?> GetByIdAsync(string id);
    Task<List<VocabularyBookUnitModel>> GetByBookIdAsync(string bookId);
    /// <summary>
    /// How many distinct meanings each unit of one book has assigned, from a
    /// single grouped read. A meaning that holds two positions of one unit —
    /// its Section A and its Section B — is one meaning, not two. Units with
    /// no assignments are absent from the dictionary, and the caller treats a
    /// missing key as zero.
    /// </summary>
    Task<Dictionary<string, int>> GetAssignmentCountsByBookIdAsync(string bookId);
    Task AddAsync(VocabularyBookUnitModel model);
    Task UpdateAsync(VocabularyBookUnitModel model);
    Task DeleteAsync(string id);
}

public interface IVocabularyMeaningUnitRepository
{
    /// <param name="section">Null for the unsectioned position of the unit.</param>
    /// <param name="entryKind">Null for the unclassified position of that place.</param>
    Task<bool> ExistsAsync(string unitId, string meaningId, string? section, string? entryKind);
    Task<List<VocabularyMeaningUnitModel>> GetByUnitIdAsync(string unitId);
    Task<List<VocabularyMeaningUnitModel>> GetByMeaningIdAsync(string meaningId);
    Task AddAsync(VocabularyMeaningUnitModel model);
    /// <param name="section">Null for the unsectioned position of the unit.</param>
    /// <param name="entryKind">Null for the unclassified position of that place.</param>
    Task DeleteAsync(string unitId, string meaningId, string? section, string? entryKind);
}
