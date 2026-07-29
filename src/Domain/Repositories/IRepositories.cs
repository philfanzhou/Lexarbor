using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Vocabulary.Domain.Models;

namespace Ruoyu.Study.Vocabulary.Domain.Repositories;

public interface IVocabularyRepository
{
    Task<VocabularyModel?> GetByIdAsync(string id);
    Task<VocabularyModel?> GetByWordAsync(string word);
    Task<VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord);
    Task<List<VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids);
    Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(string keyword, int page, int size);
    Task AddAsync(VocabularyModel model);
    Task UpdateAsync(VocabularyModel model);
    Task<List<VocabularyModel>> GetRandomExceptAsync(string excludeId, int count);
    Task<List<VocabularyModel>> GetRandomByBookExceptAsync(
        string bookId,
        string excludeVocabularyId,
        int count);
}

public interface IVocabularyBookRepository
{
    Task<VocabularyBookModel?> GetByIdAsync(string id);
    Task<List<VocabularyBookModel>> GetAllAsync();
    Task<List<VocabularyBookModel>> GetActiveAsync();
    Task<(List<VocabularyBookModel> Items, int TotalCount)> SearchAsync(string keyword, int page, int size);
    Task<List<VocabularyBookModel>> GetByCategoryAsync(string category, string? grade);
    Task<List<string>> GetDistinctCategoriesAsync();
    Task<List<string>> GetDistinctEducationLevelsAsync();
    Task<List<string>> GetDistinctGradesAsync();
    Task<List<string>> GetDistinctGradesByEducationLevelAsync(string educationLevel);
    Task<bool> HasMeaningsAsync(string bookId);
    Task<List<VocabularyModel>> GetWordsAsync(string bookId);
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
    Task<List<VocabularyMeaningModel>> GetRandomExceptAsync(string excludeVocabularyId, string bookId, int count);
    Task<VocabularyMeaningModel?> GetEquivalentAsync(
        string vocabularyId,
        string bookId,
        string normalizedPartOfSpeech,
        string meaning);
    Task<List<VocabularyMeaningModel>> GetRandomDistinctVocabularyExceptAsync(
        string bookId,
        string excludeVocabularyId,
        int count);
    Task AddAsync(VocabularyMeaningModel model);
    Task UpdateAsync(VocabularyMeaningModel model);
    Task DeleteAsync(string id);
    Task DeleteByVocabularyIdAsync(string vocabularyId);
}
