using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class MeaningSubmissionRepository : IMeaningSubmissionRepository
{
    private readonly VocabularyDbContext _context;

    public MeaningSubmissionRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<MeaningSubmissionModel?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.MeaningSubmissions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity == null ? null : ToModel(entity);
    }

    public async Task<List<MeaningSubmissionModel>> GetByIdsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.MeaningSubmissions.AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(cancellationToken);
        return entities.Select(ToModel).ToList();
    }

    public async Task<MeaningSubmissionModel?> FindReusableAsync(
        string normalizedWord,
        string normalizedPartOfSpeech,
        string meaning,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _context.MeaningSubmissions.AsNoTracking()
            .Where(item => item.NormalizedWord == normalizedWord
                           && item.NormalizedPartOfSpeech == normalizedPartOfSpeech
                           && item.NormalizedMeaning == meaning
                           && item.Status != MeaningSubmissionStatus.Rejected)
            .OrderByDescending(item => item.Status == MeaningSubmissionStatus.Pending)
            .ToListAsync(cancellationToken);
        return candidates
            .Select(ToModel)
            .Where(item => item.Status == MeaningSubmissionStatus.Pending || item.DefinitionKept)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
    }

    public async Task<(List<MeaningSubmissionModel> Items, int TotalCount)> PageAsync(
        MeaningSubmissionStatus status,
        int page,
        int size,
        CancellationToken cancellationToken = default)
    {
        var query = _context.MeaningSubmissions.AsNoTracking().Where(item => item.Status == status);
        var total = await query.CountAsync(cancellationToken);
        // Pending work is reviewed oldest first; reviewed history reads newest first.
        // Ordered by id as well, so equal timestamps page stably.
        var ordered = status == MeaningSubmissionStatus.Pending
            ? query.OrderBy(item => item.CreatedAtUnixMs).ThenBy(item => item.Id)
            : query.OrderByDescending(item => item.ReviewedAtUnixMs).ThenBy(item => item.Id);
        var entities = await ordered.Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return (entities.Select(ToModel).ToList(), total);
    }

    public async Task AddAsync(MeaningSubmissionModel submission, CancellationToken cancellationToken = default)
    {
        await _context.MeaningSubmissions.AddAsync(ToEntity(submission, new MeaningSubmissionEntity()), cancellationToken);
    }

    public async Task UpdateAsync(MeaningSubmissionModel submission, CancellationToken cancellationToken = default)
    {
        var entity = await _context.MeaningSubmissions.FindAsync([submission.Id], cancellationToken);
        if (entity != null)
        {
            ToEntity(submission, entity);
        }
    }

    public async Task<List<CatalogMeaningMatch>> SearchCatalogAsync(
        string normalizedKeyword,
        int wordLimit,
        CancellationToken cancellationToken = default)
    {
        var enabledWords = _context.Vocabularies.AsNoTracking()
            .Where(word => _context.VocabularyMeanings.Any(meaning =>
                meaning.VocabularyId == word.Id
                && _context.VocabularyBooks.Any(book => book.Id == meaning.BookId && book.Status)));
        var words = await enabledWords
            .Where(word => word.NormalizedWord == normalizedKeyword || word.NormalizedWord.StartsWith(normalizedKeyword))
            .OrderByDescending(word => word.NormalizedWord == normalizedKeyword)
            .ThenBy(word => word.NormalizedWord.Length)
            .ThenBy(word => word.NormalizedWord)
            .ThenBy(word => word.Id)
            .Take(wordLimit)
            .Select(word => new { word.Id, word.Word, word.NormalizedWord, word.PhoneticUk, word.PhoneticUs })
            .ToListAsync(cancellationToken);
        var ids = words.Select(word => word.Id).ToList();
        var meanings = await (
                from meaning in _context.VocabularyMeanings.AsNoTracking()
                join book in _context.VocabularyBooks.AsNoTracking() on meaning.BookId equals book.Id
                where book.Status && ids.Contains(meaning.VocabularyId)
                orderby book.DisplayOrder, book.BookName, book.Id, meaning.NormalizedPartOfSpeech, meaning.NormalizedMeaning
                select new { meaning.VocabularyId, meaning.Id, meaning.BookId, book.BookName, meaning.PartOfSpeech, meaning.Meaning, meaning.PhoneticUk, meaning.PhoneticUs, Key = meaning.NormalizedMeaning.ToLower() })
            .ToListAsync(cancellationToken);
        return words.SelectMany(word => meanings
                .Where(meaning => meaning.VocabularyId == word.Id)
                .Select(meaning => new CatalogMeaningMatch(
                    word.Id, word.Word, word.NormalizedWord, word.PhoneticUk, word.PhoneticUs,
                    meaning.BookId, meaning.BookName, meaning.Id, meaning.PartOfSpeech, meaning.Meaning, meaning.Key,
                    AudioSelectionService.GetPronunciation(
                        new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs),
                        new(meaning.Id, meaning.PartOfSpeech, meaning.Meaning, meaning.PhoneticUk, meaning.PhoneticUs)))))
            .ToList();
    }

    public async Task<List<VocabularyModel>> GetCatalogWordDistractorsAsync(
        string excludeNormalizedWord,
        string excludeNormalizedMeaning,
        int count,
        CancellationToken cancellationToken = default)
    {
        // A word qualifies when one of its meanings sits in an enabled book and
        // none of its meanings anywhere is the asked definition: such a word
        // would answer the question too.
        var entities = await _context.Vocabularies
            .FromSqlInterpolated($"""
                SELECT v.*
                FROM vocabulary AS v
                WHERE lower(trim(v.word)) <> {excludeNormalizedWord}
                  AND EXISTS (
                      SELECT 1
                      FROM vocabulary_meaning AS m
                      INNER JOIN vocabulary_book AS b ON b.id = m.book_id
                      WHERE m.vocabulary_id = v.id AND b.status = 1)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM vocabulary_meaning AS same
                      WHERE same.vocabulary_id = v.id
                        AND lower(trim(same.meaning)) = {excludeNormalizedMeaning})
                ORDER BY random()
                LIMIT {count * 4}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return entities
            .DistinctBy(entity => entity.Word.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Take(count)
            .Adapt<List<VocabularyModel>>();
    }

    public async Task<List<VocabularyMeaningModel>> GetCatalogMeaningDistractorsAsync(
        string excludeNormalizedWord,
        string excludeNormalizedMeaning,
        int count,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.VocabularyMeanings
            .FromSqlInterpolated($"""
                WITH per_word AS (
                    SELECT
                        m.*,
                        row_number() OVER (PARTITION BY m.vocabulary_id ORDER BY random()) AS word_rank
                    FROM vocabulary_meaning AS m
                    INNER JOIN vocabulary_book AS b ON b.id = m.book_id
                    INNER JOIN vocabulary AS v ON v.id = m.vocabulary_id
                    WHERE b.status = 1
                      AND lower(trim(v.word)) <> {excludeNormalizedWord}
                      AND lower(trim(m.meaning)) <> {excludeNormalizedMeaning})
                SELECT id, vocabulary_id, book_id, part_of_speech, phonetic_uk, phonetic_us, meaning,
                       normalized_part_of_speech, normalized_meaning, example, created_at, updated_at
                FROM per_word
                WHERE word_rank = 1
                ORDER BY random()
                LIMIT {count * 4}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return entities
            .DistinctBy(entity => entity.Meaning.Trim(), StringComparer.Ordinal)
            .Take(count)
            .Adapt<List<VocabularyMeaningModel>>();
    }

    private static MeaningSubmissionModel ToModel(MeaningSubmissionEntity entity) => new()
    {
        Id = entity.Id,
        Source = entity.Source,
        Word = entity.Word,
        PartOfSpeech = entity.PartOfSpeech,
        Meaning = entity.Meaning,
        Example = entity.Example,
        Status = entity.Status,
        ResultBookId = entity.ResultBookId,
        ResultWordId = entity.ResultWordId,
        ResultMeaningId = entity.ResultMeaningId,
        ResultWord = entity.ResultWord,
        ResultMeaning = entity.ResultMeaning,
        ResultPartOfSpeech = entity.ResultPartOfSpeech,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMs),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMs),
        ReviewedAt = entity.ReviewedAtUnixMs is { } reviewed ? DateTimeOffset.FromUnixTimeMilliseconds(reviewed) : null
    };

    private static MeaningSubmissionEntity ToEntity(MeaningSubmissionModel model, MeaningSubmissionEntity entity)
    {
        entity.Id = model.Id;
        entity.Source = model.Source;
        entity.Word = model.Word;
        entity.PartOfSpeech = model.PartOfSpeech;
        entity.Meaning = model.Meaning;
        entity.Example = model.Example;
        entity.Status = model.Status;
        entity.ResultBookId = model.ResultBookId;
        entity.ResultWordId = model.ResultWordId;
        entity.ResultMeaningId = model.ResultMeaningId;
        entity.ResultWord = model.ResultWord;
        entity.ResultMeaning = model.ResultMeaning;
        entity.ResultPartOfSpeech = model.ResultPartOfSpeech;
        entity.CreatedAtUnixMs = model.CreatedAt.ToUnixTimeMilliseconds();
        entity.UpdatedAtUnixMs = model.UpdatedAt.ToUnixTimeMilliseconds();
        entity.ReviewedAtUnixMs = model.ReviewedAt?.ToUnixTimeMilliseconds();
        return entity;
    }
}
