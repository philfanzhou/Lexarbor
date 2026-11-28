using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public class VocabularyMeaningRepository : IVocabularyMeaningRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyMeaningRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyMeaning?> GetByIdAsync(string id)
    {
        // Preserve current ownership even if this context previously tracked the meaning.
        var entity = await _context.VocabularyMeanings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
        return entity?.Adapt<VocabularyMeaning>();
    }

    public async Task<List<VocabularyMeaning>> GetByVocabularyIdAsync(string vocabularyId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.VocabularyId == vocabularyId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaning>>();
    }

    public async Task<List<VocabularyMeaning>> GetByBookIdAsync(string bookId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.BookId == bookId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaning>>();
    }

    public Task<List<VocabularyMeaning>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId)
        => GetByBookAndVocabularyIdAsync(bookId, vocabularyId, CancellationToken.None);

    public async Task<List<VocabularyMeaning>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId, CancellationToken cancellationToken)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.BookId == bookId && m.VocabularyId == vocabularyId)
            .ToListAsync(cancellationToken);
        return entities.Adapt<List<VocabularyMeaning>>();
    }

    public async Task<VocabularyMeaning?> GetEquivalentAsync(
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
        return entity?.Adapt<VocabularyMeaning>();
    }

    public Task<List<VocabularyMeaning>> GetRandomDistinctVocabularyExceptAsync(
        string bookId, string excludeVocabularyId, string excludeMeaning, int count, VocabularyDistractorScope? scope = null)
        => GetRandomDistinctVocabularyExceptAsync(bookId, excludeVocabularyId, excludeMeaning, count, scope, CancellationToken.None);

    public async Task<List<VocabularyMeaning>> GetRandomDistinctVocabularyExceptAsync(
        string bookId,
        string excludeVocabularyId,
        string excludeMeaning,
        int count,
        VocabularyDistractorScope? scope, CancellationToken cancellationToken)
    {
        var normalizedExcludeMeaning = excludeMeaning.Trim().ToLowerInvariant();
        var unitId = scope?.UnitId;
        var phraseOnly = scope?.PhraseOnly;

        var window = await ReadCandidateWindowAsync(
            bookId,
            excludeVocabularyId,
            normalizedExcludeMeaning,
            RandomCandidateWindow.NewProbe(),
            RandomCandidateWindow.SizeFor(count),
            unitId,
            phraseOnly, cancellationToken);
        if (CountDistinctVocabulary(window) < count)
        {
            window = RandomCandidateWindow.Merge(
                window,
                await ReadCandidateWindowAsync(
                    bookId,
                    excludeVocabularyId,
                    normalizedExcludeMeaning,
                    string.Empty,
                    RandomCandidateWindow.SizeFor(count),
                    unitId,
                    phraseOnly, cancellationToken),
                meaning => meaning.Id);
        }

        var selected = SelectOneMeaningPerWord(window, count);
        if (selected.Count == count)
        {
            return selected.Adapt<List<VocabularyMeaning>>();
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
                      AND ({unitId}::text IS NULL OR EXISTS (
                          SELECT 1
                          FROM vocabulary_meaning_unit AS scoped_unit
                          WHERE scoped_unit.meaning_id = m.id
                            AND scoped_unit.unit_id = {unitId}
                      ))
                      AND ({phraseOnly}::boolean IS NULL OR EXISTS (
                          SELECT 1
                          FROM vocabulary_meaning_unit AS scoped_unit
                          WHERE scoped_unit.meaning_id = m.id
                            AND ({unitId}::text IS NOT NULL AND scoped_unit.unit_id = {unitId}
                                 OR {unitId}::text IS NULL)
                            AND scoped_unit.entry_kind = 'phrase'
                      ) = {phraseOnly}::boolean)
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
                    phonetic_uk,
                    phonetic_us,
                    meaning,
                    normalized_part_of_speech,
                    normalized_meaning,
                    example,
                    created_at,
                    updated_at,
                    insertion_order
                FROM distinct_meaning
                WHERE meaning_rank = 1
                ORDER BY random()
                LIMIT {count}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return entities.Adapt<List<VocabularyMeaning>>();
    }

    /// <summary>
    /// Reads every definition of the words in one window of the book. The window
    /// is bounded, so this is a handful of rows rather than the whole book.
    /// </summary>
    /// <param name="unitId">
    /// With <paramref name="phraseOnly"/>, the two switch parameters of the
    /// distractor scope, spelled the same way the word direction spells them:
    /// both null leaves the added conditions vacuously true, and the constant
    /// SQL text means the scope narrows this query without reshaping it.
    /// </param>
    /// <param name="phraseOnly">
    /// The entry-kind filter of the scope. The candidates of this direction are
    /// meanings, so the kind is read straight off each candidate's own
    /// positions — scoped to the unit when one was named, to the meaning's book
    /// otherwise, which the membership's foreign keys already confine it to.
    /// </param>
    private Task<List<VocabularyMeaningEntity>> ReadCandidateWindowAsync(
        string bookId,
        string excludeVocabularyId,
        string normalizedExcludeMeaning,
        string probe,
        int limit,
        string? unitId = null,
        bool? phraseOnly = null, CancellationToken cancellationToken = default)
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
                          AND ({unitId}::text IS NULL OR EXISTS (
                              SELECT 1
                              FROM vocabulary_meaning_unit AS scoped_unit
                              WHERE scoped_unit.meaning_id = candidate.id
                                AND scoped_unit.unit_id = {unitId}
                          ))
                          AND ({phraseOnly}::boolean IS NULL OR EXISTS (
                              SELECT 1
                              FROM vocabulary_meaning_unit AS scoped_unit
                              WHERE scoped_unit.meaning_id = candidate.id
                                AND ({unitId}::text IS NOT NULL AND scoped_unit.unit_id = {unitId}
                                     OR {unitId}::text IS NULL)
                                AND scoped_unit.entry_kind = 'phrase'
                          ) = {phraseOnly}::boolean)
                        GROUP BY candidate.vocabulary_id
                        ORDER BY candidate.vocabulary_id
                        LIMIT {limit}
                    )
                  AND lower(trim(m.meaning)) <> {normalizedExcludeMeaning}
                  AND ({unitId}::text IS NULL OR EXISTS (
                      SELECT 1
                      FROM vocabulary_meaning_unit AS scoped_unit
                      WHERE scoped_unit.meaning_id = m.id
                        AND scoped_unit.unit_id = {unitId}
                  ))
                  AND ({phraseOnly}::boolean IS NULL OR EXISTS (
                      SELECT 1
                      FROM vocabulary_meaning_unit AS scoped_unit
                      WHERE scoped_unit.meaning_id = m.id
                        AND ({unitId}::text IS NOT NULL AND scoped_unit.unit_id = {unitId}
                             OR {unitId}::text IS NULL)
                        AND scoped_unit.entry_kind = 'phrase'
                  ) = {phraseOnly}::boolean)
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
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

    public async Task AddAsync(VocabularyMeaning model)
    {
        var entity = model.Adapt<VocabularyMeaningEntity>();
        await _context.VocabularyMeanings.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyMeaning model)
    {
        var entity = await _context.VocabularyMeanings.FindAsync(model.Id);
        if (entity != null)
        {
            var phonetics = await _context.VocabularyMeanings.AsNoTracking().Where(row => row.Id == model.Id)
                .Select(row => new { row.PhoneticUk, row.PhoneticUs }).SingleAsync();
            model.Adapt(entity);
            entity.PhoneticUk = phonetics.PhoneticUk;
            entity.PhoneticUs = phonetics.PhoneticUs;
            _context.VocabularyMeanings.Update(entity);
            // Specific phonetics have a separate atomic invalidation boundary.
            // General meaning edits must also preserve values read by an older
            // tracked instance before a concurrent phonetic transaction.
            _context.Entry(entity).Property(row => row.PhoneticUk).IsModified = false;
            _context.Entry(entity).Property(row => row.PhoneticUs).IsModified = false;
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
