using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public class VocabularyRepository : IVocabularyRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public Task<Vocabulary?> GetByIdAsync(string id) => GetByIdAsync(id, CancellationToken.None);

    public async Task<Vocabulary?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        // Transactional editors must see the current row, not an earlier tracked snapshot.
        var entity = await _context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        return entity?.Adapt<Vocabulary>();
    }

    /// <summary>
    /// Resolves a normalized word to the row that carries it.
    /// </summary>
    /// <remarks>
    /// The predicate used to be written as <c>Word.Trim().ToLower()</c>, which
    /// SQLite answers as <c>lower(trim(word))</c>: a function around the column,
    /// so <c>IX_vocabulary_word</c> could not serve it and every call scanned the
    /// table. The comparison now lands on the generated <c>normalized_word</c>
    /// column, which holds the same expression and is indexed, so the same
    /// question is answered by a seek. The semantics are unchanged -- it is
    /// literally the same SQL expression.
    /// </remarks>
    public async Task<Vocabulary?> GetByNormalizedWordAsync(string normalizedWord)
    {
        var entity = await _context.Vocabularies
            .FirstOrDefaultAsync(vocabulary => vocabulary.NormalizedWord == normalizedWord);
        return entity?.Adapt<Vocabulary>();
    }

    public async Task<(List<Vocabulary> Items, int TotalCount)> SearchAsync(string? keyword, int page, int size)
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
            var pattern = SearchPattern.Contains(keyword);
            query = query.Where(v =>
                EF.Functions.ILike(v.Word, pattern, SearchPattern.EscapeCharacter.ToString()));
        }

        var totalCount = await query.CountAsync();
        var entities = await query.OrderBy(v => v.Word)
                                  .Skip((page - 1) * size)
                                  .Take(size)
                                  .ToListAsync();
        return (entities.Adapt<List<Vocabulary>>(), totalCount);
    }

    public async Task AddAsync(Vocabulary model)
    {
        var entity = model.Adapt<VocabularyEntity>();
        await _context.Vocabularies.AddAsync(entity);
    }

    public async Task UpdateAsync(Vocabulary model)
    {
        var entity = await _context.Vocabularies.FindAsync(model.Id);
        if (entity != null)
        {
            model.Adapt(entity);
            _context.Vocabularies.Update(entity);
        }
    }

    public Task<List<Vocabulary>> GetRandomByBookExceptAsync(
        string bookId, string excludeVocabularyId, string excludeWord, string excludeEquivalentMeaning, int count, VocabularyDistractorScope? scope = null)
        => GetRandomByBookExceptAsync(bookId, excludeVocabularyId, excludeWord, excludeEquivalentMeaning, count, scope, CancellationToken.None);

    public async Task<List<Vocabulary>> GetRandomByBookExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeWord,
        string excludeEquivalentMeaning,
        int count,
        VocabularyDistractorScope? scope, CancellationToken cancellationToken)
    {
        var normalizedExcludeWord = excludeWord.Trim().ToLowerInvariant();
        var normalizedExcludeMeaning = excludeEquivalentMeaning.Trim().ToLowerInvariant();
        var unitId = scope?.UnitId;
        var phraseOnly = scope?.PhraseOnly;

        var window = await ReadCandidateWindowAsync(
            bookId,
            excludeVocabularyId,
            normalizedExcludeWord,
            normalizedExcludeMeaning,
            RandomCandidateWindow.NewProbe(),
            RandomCandidateWindow.SizeFor(count),
            unitId,
            phraseOnly, cancellationToken);
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
                    RandomCandidateWindow.SizeFor(count),
                    unitId,
                    phraseOnly, cancellationToken),
                entity => entity.Id);
        }

        var selected = RandomCandidateWindow
            .Shuffle(window)
            .DistinctBy(entity => entity.Word.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Take(count)
            .ToList();
        if (selected.Count == count)
        {
            return selected.Adapt<List<Vocabulary>>();
        }

        // The window did not fill. Either the book is nearly out of candidates
        // or an unlucky run of duplicates ate the window, and the caller answers
        // 422 on a short result, so falling back to the exhaustive scan keeps
        // that answer meaning what it did before rather than becoming a function
        // of where the probe landed.
        var entities = await _context.Vocabularies
            .FromSqlInterpolated($"""
                SELECT *
                FROM (
                SELECT DISTINCT ON (lower(trim(v.word))) v.*
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
                  AND ({unitId}::text IS NULL OR EXISTS (
                      SELECT 1
                      FROM vocabulary_meaning AS scoped_meaning
                      INNER JOIN vocabulary_meaning_unit AS scoped_unit
                          ON scoped_unit.meaning_id = scoped_meaning.id
                      WHERE scoped_meaning.book_id = {bookId}
                        AND scoped_meaning.vocabulary_id = v.id
                        AND scoped_unit.unit_id = {unitId}
                  ))
                  AND ({phraseOnly}::boolean IS NULL OR EXISTS (
                      SELECT 1
                      FROM vocabulary_meaning AS scoped_meaning
                      INNER JOIN vocabulary_meaning_unit AS scoped_unit
                          ON scoped_unit.meaning_id = scoped_meaning.id
                      WHERE scoped_meaning.book_id = {bookId}
                        AND scoped_meaning.vocabulary_id = v.id
                        AND ({unitId}::text IS NOT NULL AND scoped_unit.unit_id = {unitId}
                             OR {unitId}::text IS NULL)
                        AND scoped_unit.entry_kind = 'phrase'
                  ) = {phraseOnly}::boolean)
                -- One row per spelling, chosen at random: the row SQLite's bare
                -- GROUP BY column used to return was just as arbitrary.
                ORDER BY lower(trim(v.word)), random()
                ) AS one_per_spelling
                ORDER BY random()
                LIMIT {count}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return entities.Adapt<List<Vocabulary>>();
    }

    /// <summary>
    /// Reads consecutive candidate words starting at <paramref name="probe"/> in
    /// id order. The filtering is identical to the exhaustive query above; only
    /// the amount of the book it touches differs.
    /// </summary>
    /// <param name="unitId">
    /// With <paramref name="phraseOnly"/>, the two switch parameters of the
    /// distractor scope. Both null — the request named no scope — leaves every
    /// added condition vacuously true, so the query answers exactly what it did
    /// before the scope existed. They are bound as one constant SQL text rather
    /// than spliced, so the six distractor query shapes stay six and the scope
    /// cannot change the shape it rides on.
    /// </param>
    /// <param name="phraseOnly">
    /// The entry-kind filter of the scope. Its subquery reads the candidate
    /// word's kind from the same scope the unit filter names — that unit, or
    /// the whole book when no unit was named — and one phrase position there
    /// makes the word a phrase. The comparison against the parameter keeps
    /// phrase words when it is 1 and drops them when it is 0.
    /// </param>
    private Task<List<VocabularyEntity>> ReadCandidateWindowAsync(
        string bookId,
        string excludeVocabularyId,
        string normalizedExcludeWord,
        string normalizedExcludeMeaning,
        string probe,
        int limit,
        string? unitId = null,
        bool? phraseOnly = null, CancellationToken cancellationToken = default)
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
                          AND ({unitId}::text IS NULL OR EXISTS (
                              SELECT 1
                              FROM vocabulary_meaning AS scoped_meaning
                              INNER JOIN vocabulary_meaning_unit AS scoped_unit
                                  ON scoped_unit.meaning_id = scoped_meaning.id
                              WHERE scoped_meaning.book_id = {bookId}
                                AND scoped_meaning.vocabulary_id = m.vocabulary_id
                                AND scoped_unit.unit_id = {unitId}
                          ))
                          AND ({phraseOnly}::boolean IS NULL OR EXISTS (
                              SELECT 1
                              FROM vocabulary_meaning AS scoped_meaning
                              INNER JOIN vocabulary_meaning_unit AS scoped_unit
                                  ON scoped_unit.meaning_id = scoped_meaning.id
                              WHERE scoped_meaning.book_id = {bookId}
                                AND scoped_meaning.vocabulary_id = m.vocabulary_id
                                AND ({unitId}::text IS NOT NULL AND scoped_unit.unit_id = {unitId}
                                     OR {unitId}::text IS NULL)
                                AND scoped_unit.entry_kind = 'phrase'
                          ) = {phraseOnly}::boolean)
                        GROUP BY m.vocabulary_id
                        ORDER BY m.vocabulary_id
                        LIMIT {limit}
                    )
                  AND lower(trim(v.word)) <> {normalizedExcludeWord}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
