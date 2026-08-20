using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

/// <summary>
/// Builds the substring patterns used by the keyword searches.
/// </summary>
/// <remarks>
/// <c>string.Contains</c> translates to SQLite's <c>instr()</c>, which compares
/// bytes, so a keyword had to match the stored casing exactly. Words are always
/// stored lower-cased, which made every mixed-case public search return an empty
/// page rather than an error. <c>LIKE</c> folds ASCII case by default -- nothing
/// in this application sets <c>PRAGMA case_sensitive_like</c> -- and that is the
/// same folding <c>lower()</c> already gives the question and equivalence
/// queries, so the whole codebase now agrees on what "the same text" means.
/// Neither operator can use an index for a leading-wildcard match, so this is
/// not a change in query cost.
/// </remarks>
internal static class SqliteSearchPattern
{
    /// <summary>Backslash, declared to SQLite with an explicit ESCAPE clause.</summary>
    internal const char EscapeCharacter = '\\';

    /// <summary>
    /// Wraps a user-supplied keyword in wildcards, escaping the characters LIKE
    /// would otherwise read as wildcards. Without this, a keyword containing
    /// <c>%</c> or <c>_</c> would silently widen the search -- something
    /// <c>instr()</c> never did.
    /// </summary>
    internal static string Contains(string keyword)
    {
        var escaped = keyword
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        return $"%{escaped}%";
    }
}

public class VocabularyRepository : IVocabularyRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyModel?> GetByIdAsync(string id)
    {
        var entity = await _context.Vocabularies.FindAsync(id);
        return entity?.Adapt<VocabularyModel>();
    }

    public async Task<VocabularyModel?> GetByWordAsync(string word)
    {
        var entity = await _context.Vocabularies.FirstOrDefaultAsync(v => v.Word == word);
        return entity?.Adapt<VocabularyModel>();
    }

    public async Task<VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord)
    {
        var entity = await _context.Vocabularies
            .FirstOrDefaultAsync(vocabulary => vocabulary.Word.Trim().ToLower() == normalizedWord);
        return entity?.Adapt<VocabularyModel>();
    }

    public async Task<List<VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids)
    {
        if (ids == null || ids.Count == 0)
            return new List<VocabularyModel>();

        var entities = await _context.Vocabularies
            .Where(v => ids.Contains(v.Id))
            .ToListAsync();
        return entities.Adapt<List<VocabularyModel>>();
    }

    public async Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(string? keyword, int page, int size)
    {
        var query = _context.Vocabularies
            .AsNoTracking()
            .Where(vocabulary =>
                _context.VocabularyMeanings.Any(meaning =>
                    meaning.VocabularyId == vocabulary.Id &&
                    _context.VocabularyBooks.Any(book =>
                        book.Id == meaning.BookId &&
                        book.Status)));
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = SqliteSearchPattern.Contains(keyword);
            query = query.Where(v =>
                EF.Functions.Like(v.Word, pattern, SqliteSearchPattern.EscapeCharacter.ToString()));
        }

        var totalCount = await query.CountAsync();
        var entities = await query.OrderBy(v => v.Word)
                                  .Skip((page - 1) * size)
                                  .Take(size)
                                  .ToListAsync();
        return (entities.Adapt<List<VocabularyModel>>(), totalCount);
    }

    public async Task AddAsync(VocabularyModel model)
    {
        var entity = model.Adapt<VocabularyEntity>();
        await _context.Vocabularies.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyModel model)
    {
        var entity = await _context.Vocabularies.FindAsync(model.Id);
        if (entity != null)
        {
            model.Adapt(entity);
            _context.Vocabularies.Update(entity);
        }
    }

    public async Task<List<VocabularyModel>> GetRandomByBookExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeWord,
        string excludeEquivalentMeaning,
        int count)
    {
        var normalizedExcludeWord = excludeWord.Trim().ToLowerInvariant();
        var normalizedExcludeMeaning = excludeEquivalentMeaning.Trim().ToLowerInvariant();
        var entities = await _context.Vocabularies
            .FromSqlInterpolated($"""
                SELECT v.*
                FROM vocabulary AS v
                INNER JOIN vocabulary_meaning AS m
                    ON m.vocabulary_id = v.id
                WHERE m.book_id = {bookId}
                  AND v.id <> {excludeVocabularyId}
                  AND lower(trim(v.word)) <> {normalizedExcludeWord}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM vocabulary_meaning AS synonym
                      WHERE synonym.vocabulary_id = v.id
                        AND synonym.book_id = {bookId}
                        AND lower(trim(synonym.meaning)) = {normalizedExcludeMeaning}
                  )
                GROUP BY lower(trim(v.word))
                ORDER BY random()
                LIMIT {count}
                """)
            .AsNoTracking()
            .ToListAsync();

        return entities.Adapt<List<VocabularyModel>>();
    }
}

public class VocabularyBookRepository : IVocabularyBookRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyBookRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyBookModel?> GetByIdAsync(string id)
    {
        var entity = await _context.VocabularyBooks
            .AsNoTracking()
            .FirstOrDefaultAsync(book => book.Id == id);
        return entity?.Adapt<VocabularyBookModel>();
    }

    public async Task<List<VocabularyBookModel>> GetAllAsync()
    {
        var entities = await _context.VocabularyBooks
            .AsNoTracking()
            .OrderBy(book => book.DisplayOrder)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBookModel>>();
    }

    public async Task<List<VocabularyBookModel>> GetActiveAsync()
    {
        var entities = await _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.Status)
            .OrderBy(book => book.DisplayOrder)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBookModel>>();
    }

    public async Task<(List<VocabularyBookModel> Items, int TotalCount)> SearchAsync(
        string? keyword,
        int page,
        int size)
    {
        var query = _context.VocabularyBooks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = SqliteSearchPattern.Contains(keyword);
            var escape = SqliteSearchPattern.EscapeCharacter.ToString();
            query = query.Where(book =>
                EF.Functions.Like(book.BookName, pattern, escape) ||
                (book.Description != null &&
                 EF.Functions.Like(book.Description, pattern, escape)));
        }

        var totalCount = await query.CountAsync();
        var entities = await query
            .OrderBy(book => book.DisplayOrder)
            .ThenBy(book => book.BookName)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();
        return (entities.Adapt<List<VocabularyBookModel>>(), totalCount);
    }

    public async Task<List<VocabularyBookModel>> GetByCategoryAsync(string? category, string? grade)
    {
        var query = _context.VocabularyBooks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(book => book.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(grade))
        {
            query = query.Where(book => book.Grade == grade);
        }

        var entities = await query
            .OrderBy(book => book.DisplayOrder)
            .ThenBy(book => book.BookName)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBookModel>>();
    }

    public Task<List<string>> GetDistinctCategoriesAsync()
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.Category != null && book.Category != string.Empty)
            .Select(book => book.Category!)
            .Distinct()
            .OrderBy(category => category)
            .ToListAsync();
    }

    public Task<List<string>> GetDistinctEducationLevelsAsync()
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.EducationLevel != null && book.EducationLevel != string.Empty)
            .Select(book => book.EducationLevel!)
            .Distinct()
            .OrderBy(educationLevel => educationLevel)
            .ToListAsync();
    }

    public Task<List<string>> GetDistinctGradesAsync()
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.Grade != null && book.Grade != string.Empty)
            .Select(book => book.Grade!)
            .Distinct()
            .OrderBy(grade => grade)
            .ToListAsync();
    }

    public Task<List<string>> GetDistinctGradesByEducationLevelAsync(string educationLevel)
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book =>
                book.EducationLevel == educationLevel &&
                book.Grade != null &&
                book.Grade != string.Empty)
            .Select(book => book.Grade!)
            .Distinct()
            .OrderBy(grade => grade)
            .ToListAsync();
    }

    public Task<bool> HasMeaningsAsync(string bookId)
    {
        return _context.VocabularyMeanings
            .AsNoTracking()
            .AnyAsync(meaning => meaning.BookId == bookId);
    }

    public async Task<List<VocabularyModel>> GetWordsAsync(string bookId)
    {
        var entities = await _context.VocabularyMeanings
            .AsNoTracking()
            .Where(meaning => meaning.BookId == bookId)
            .Join(
                _context.Vocabularies.AsNoTracking(),
                meaning => meaning.VocabularyId,
                vocabulary => vocabulary.Id,
                (_, vocabulary) => vocabulary)
            .Distinct()
            .OrderBy(vocabulary => vocabulary.Word)
            .ToListAsync();
        return entities.Adapt<List<VocabularyModel>>();
    }

    public async Task AddAsync(VocabularyBookModel model)
    {
        var entity = model.Adapt<VocabularyBookEntity>();
        await _context.VocabularyBooks.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyBookModel model)
    {
        var entity = await _context.VocabularyBooks.FindAsync(model.Id);
        if (entity != null)
        {
            model.Adapt(entity);
            _context.VocabularyBooks.Update(entity);
        }
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.VocabularyBooks.FindAsync(id);
        if (entity != null)
        {
            _context.VocabularyBooks.Remove(entity);
        }
    }
}

public class VocabularyMeaningRepository : IVocabularyMeaningRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyMeaningRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyMeaningModel?> GetByIdAsync(string id)
    {
        var entity = await _context.VocabularyMeanings.FindAsync(id);
        return entity?.Adapt<VocabularyMeaningModel>();
    }

    public async Task<List<VocabularyMeaningModel>> GetByVocabularyIdAsync(string vocabularyId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.VocabularyId == vocabularyId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task<List<VocabularyMeaningModel>> GetByBookIdAsync(string bookId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.BookId == bookId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task<List<VocabularyMeaningModel>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.BookId == bookId && m.VocabularyId == vocabularyId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task<VocabularyMeaningModel?> GetEquivalentAsync(
        string vocabularyId,
        string bookId,
        string normalizedPartOfSpeech,
        string meaning)
    {
        var normalizedMeaning = meaning.Trim();
        var entity = await _context.VocabularyMeanings.FirstOrDefaultAsync(item =>
            item.VocabularyId == vocabularyId &&
            item.BookId == bookId &&
            (item.PartOfSpeech ?? string.Empty).Trim().ToLower() == normalizedPartOfSpeech &&
            item.Meaning.Trim() == normalizedMeaning);
        return entity?.Adapt<VocabularyMeaningModel>();
    }

    public async Task<List<VocabularyMeaningModel>> GetRandomDistinctVocabularyExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeMeaning,
        int count)
    {
        var normalizedExcludeMeaning = excludeMeaning.Trim().ToLowerInvariant();
        var entities = await _context.VocabularyMeanings
            .FromSqlInterpolated($"""
                WITH per_word AS (
                    SELECT
                        m.*,
                        row_number() OVER (
                            PARTITION BY m.vocabulary_id
                            ORDER BY random()) AS word_rank
                    FROM vocabulary_meaning AS m
                    WHERE m.book_id = {bookId}
                      AND m.vocabulary_id <> {excludeVocabularyId}
                      AND lower(trim(m.meaning)) <> {normalizedExcludeMeaning}
                ),
                distinct_meaning AS (
                    SELECT
                        per_word.*,
                        row_number() OVER (
                            PARTITION BY lower(trim(per_word.meaning))
                            ORDER BY random()) AS meaning_rank
                    FROM per_word
                    WHERE word_rank = 1
                )
                SELECT
                    id,
                    vocabulary_id,
                    book_id,
                    part_of_speech,
                    meaning,
                    normalized_part_of_speech,
                    normalized_meaning,
                    example,
                    created_at,
                    updated_at
                FROM distinct_meaning
                WHERE meaning_rank = 1
                ORDER BY random()
                LIMIT {count}
                """)
            .AsNoTracking()
            .ToListAsync();

        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task AddAsync(VocabularyMeaningModel model)
    {
        var entity = model.Adapt<VocabularyMeaningEntity>();
        await _context.VocabularyMeanings.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyMeaningModel model)
    {
        var entity = await _context.VocabularyMeanings.FindAsync(model.Id);
        if (entity != null)
        {
            model.Adapt(entity);
            _context.VocabularyMeanings.Update(entity);
        }
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.VocabularyMeanings.FindAsync(id);
        if (entity != null)
        {
            _context.VocabularyMeanings.Remove(entity);
        }
    }

    public async Task DeleteByVocabularyIdAsync(string vocabularyId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.VocabularyId == vocabularyId)
            .ToListAsync();
        _context.VocabularyMeanings.RemoveRange(entities);
    }
}
