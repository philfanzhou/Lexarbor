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

/// <summary>
/// Draws a small random window of question candidates instead of sorting a whole
/// vocabulary book.
/// </summary>
/// <remarks>
/// <para>
/// Both distractor queries used <c>ORDER BY random()</c> over every candidate in
/// the book, which SQLite can only answer with a temp B-tree over all of them,
/// so the cost of one anonymous question grew with the size of the book it was
/// asked about. Measured before this change: 2.2 ms at 300 words, 5.4 ms at
/// 2000, and 56 ms at 20000 -- and 129 ms for the English-to-Chinese direction,
/// which also ran two window functions across the same rows.
/// </para>
/// <para>
/// Every vocabulary id in this database is a v4 GUID, written that way by the
/// domain service and by the bundled seed alike, so id order is already an
/// arbitrary and uniform shuffle of the book. Starting at a random point in that
/// order and reading forward is therefore a random sample, and it is one an
/// index can walk: the window is a seek plus a handful of rows rather than a
/// sort of everything.
/// </para>
/// </remarks>
internal static class RandomCandidateWindow
{
    /// <summary>
    /// Candidates read for each one returned. The window is shuffled and then
    /// truncated, so over-fetching is what keeps the three options from being
    /// three consecutive ids -- which would pair the same words together in
    /// every question they appeared in.
    /// </summary>
    private const int OverfetchFactor = 4;

    internal static int SizeFor(int count) => count * OverfetchFactor;

    /// <summary>
    /// A random point in the id space to start reading from. A GUID rather than
    /// a number because that is the shape of the column being compared.
    /// </summary>
    internal static string NewProbe() => Guid.NewGuid().ToString();

    /// <summary>
    /// Appends the rows of a wrapped-around second window that the first did not
    /// already contain.
    /// </summary>
    internal static List<T> Merge<T>(List<T> first, List<T> second, Func<T, string> keySelector)
    {
        var seen = new HashSet<string>(first.Select(keySelector), StringComparer.Ordinal);
        first.AddRange(second.Where(item => seen.Add(keySelector(item))));
        return first;
    }

    /// <summary>Fisher-Yates, in place.</summary>
    internal static List<T> Shuffle<T>(List<T> items)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapWith = Random.Shared.Next(index + 1);
            (items[index], items[swapWith]) = (items[swapWith], items[index]);
        }

        return items;
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

        var window = await ReadCandidateWindowAsync(
            bookId,
            excludeVocabularyId,
            normalizedExcludeWord,
            normalizedExcludeMeaning,
            RandomCandidateWindow.NewProbe(),
            RandomCandidateWindow.SizeFor(count));
        if (window.Count < count)
        {
            // The probe landed near the end of the id space. Wrapping to the
            // start is what makes the window complete: when the whole pool fits
            // in one window, this reads all of it.
            window = RandomCandidateWindow.Merge(
                window,
                await ReadCandidateWindowAsync(
                    bookId,
                    excludeVocabularyId,
                    normalizedExcludeWord,
                    normalizedExcludeMeaning,
                    string.Empty,
                    RandomCandidateWindow.SizeFor(count)),
                entity => entity.Id);
        }

        var selected = RandomCandidateWindow
            .Shuffle(window)
            .DistinctBy(entity => entity.Word.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Take(count)
            .ToList();
        if (selected.Count == count)
        {
            return selected.Adapt<List<VocabularyModel>>();
        }

        // The window did not fill. Either the book is nearly out of candidates
        // or an unlucky run of duplicates ate the window, and the caller answers
        // 422 on a short result, so falling back to the exhaustive scan keeps
        // that answer meaning what it did before rather than becoming a function
        // of where the probe landed.
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

    /// <summary>
    /// Reads consecutive candidate words starting at <paramref name="probe"/> in
    /// id order. The filtering is identical to the exhaustive query above; only
    /// the amount of the book it touches differs.
    /// </summary>
    private Task<List<VocabularyEntity>> ReadCandidateWindowAsync(
        string bookId,
        string excludeVocabularyId,
        string normalizedExcludeWord,
        string normalizedExcludeMeaning,
        string probe,
        int limit)
    {
        return _context.Vocabularies
            .FromSqlInterpolated($"""
                SELECT v.*
                FROM vocabulary AS v
                WHERE v.id IN (
                        SELECT m.vocabulary_id
                        FROM vocabulary_meaning AS m
                        WHERE m.book_id = {bookId}
                          AND m.vocabulary_id >= {probe}
                          AND m.vocabulary_id <> {excludeVocabularyId}
                          AND NOT EXISTS (
                              SELECT 1
                              FROM vocabulary_meaning AS synonym
                              WHERE synonym.vocabulary_id = m.vocabulary_id
                                AND synonym.book_id = {bookId}
                                AND lower(trim(synonym.meaning)) = {normalizedExcludeMeaning}
                          )
                        GROUP BY m.vocabulary_id
                        ORDER BY m.vocabulary_id
                        LIMIT {limit}
                    )
                  AND lower(trim(v.word)) <> {normalizedExcludeWord}
                """)
            .AsNoTracking()
            .ToListAsync();
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

        var window = await ReadCandidateWindowAsync(
            bookId,
            excludeVocabularyId,
            normalizedExcludeMeaning,
            RandomCandidateWindow.NewProbe(),
            RandomCandidateWindow.SizeFor(count));
        if (CountDistinctVocabulary(window) < count)
        {
            window = RandomCandidateWindow.Merge(
                window,
                await ReadCandidateWindowAsync(
                    bookId,
                    excludeVocabularyId,
                    normalizedExcludeMeaning,
                    string.Empty,
                    RandomCandidateWindow.SizeFor(count)),
                meaning => meaning.Id);
        }

        var selected = SelectOneMeaningPerWord(window, count);
        if (selected.Count == count)
        {
            return selected.Adapt<List<VocabularyMeaningModel>>();
        }

        // Same reasoning as the word direction: a short window must not become a
        // 422 that the exhaustive query would not have produced.
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

    /// <summary>
    /// Reads every definition of the words in one window of the book. The window
    /// is bounded, so this is a handful of rows rather than the whole book.
    /// </summary>
    private Task<List<VocabularyMeaningEntity>> ReadCandidateWindowAsync(
        string bookId,
        string excludeVocabularyId,
        string normalizedExcludeMeaning,
        string probe,
        int limit)
    {
        return _context.VocabularyMeanings
            .FromSqlInterpolated($"""
                SELECT m.*
                FROM vocabulary_meaning AS m
                WHERE m.book_id = {bookId}
                  AND m.vocabulary_id IN (
                        SELECT candidate.vocabulary_id
                        FROM vocabulary_meaning AS candidate
                        WHERE candidate.book_id = {bookId}
                          AND candidate.vocabulary_id >= {probe}
                          AND candidate.vocabulary_id <> {excludeVocabularyId}
                        GROUP BY candidate.vocabulary_id
                        ORDER BY candidate.vocabulary_id
                        LIMIT {limit}
                    )
                  AND lower(trim(m.meaning)) <> {normalizedExcludeMeaning}
                """)
            .AsNoTracking()
            .ToListAsync();
    }

    private static int CountDistinctVocabulary(List<VocabularyMeaningEntity> candidates)
    {
        return candidates
            .Select(meaning => meaning.VocabularyId)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    /// <summary>
    /// Applies the two rules the exhaustive query expresses with window
    /// functions: one option per word, drawn at random from that word's
    /// definitions, and then no two options carrying the same text.
    /// </summary>
    private static List<VocabularyMeaningEntity> SelectOneMeaningPerWord(
        List<VocabularyMeaningEntity> candidates,
        int count)
    {
        var chosen = new List<VocabularyMeaningEntity>(count);
        var usedTexts = new HashSet<string>(StringComparer.Ordinal);
        var words = candidates
            .GroupBy(meaning => meaning.VocabularyId, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .ToList();

        foreach (var word in RandomCandidateWindow.Shuffle(words))
        {
            var meaning = RandomCandidateWindow.Shuffle(word)[0];

            // A word whose drawn definition collides with one already chosen is
            // dropped rather than asked for another, which is what the
            // exhaustive query's second window function does.
            if (usedTexts.Add(meaning.Meaning.Trim().ToLowerInvariant()))
            {
                chosen.Add(meaning);
            }

            if (chosen.Count == count)
            {
                break;
            }
        }

        return chosen;
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
