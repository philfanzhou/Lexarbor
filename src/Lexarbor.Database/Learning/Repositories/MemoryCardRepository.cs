using Microsoft.EntityFrameworkCore;
using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Database.Repositories;

public class MemoryCardRepository : IMemoryCardRepository
{
    private readonly LearningDbContext _dbContext;

    public MemoryCardRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MemoryCardModel?> FindAsync(Guid studentId, string wordKey, string meaningKey, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.MemoryCards.FindAsync(
            new object[] { studentId, wordKey, meaningKey }, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public Task AddAsync(MemoryCardModel card, CancellationToken cancellationToken = default)
    {
        return _dbContext.MemoryCards.AddAsync(ToEntity(card), cancellationToken).AsTask();
    }

    public Task<bool> HasWordAsync(Guid studentId, string wordKey, CancellationToken cancellationToken = default) =>
        _dbContext.MemoryCards.AnyAsync(card => card.StudentId == studentId && card.WordKey == wordKey, cancellationToken);

    public async Task UpdateAsync(MemoryCardModel card, CancellationToken cancellationToken = default)
    {


        var entity = await _dbContext.MemoryCards.FindAsync(
            new object[] { card.StudentId, card.WordKey, card.MeaningKey }, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException(
                $"Memory card ({card.WordKey}, {card.MeaningKey}) for student {card.StudentId} is not loaded and cannot be updated.");
        }

        entity.State = (int)card.State;
        entity.Stability = card.Stability;
        entity.Difficulty = card.Difficulty;
        entity.DueAt = card.DueAt;
        entity.Reps = card.Reps;
        entity.Lapses = card.Lapses;
        entity.LastReviewedAt = card.LastReviewedAt!.Value;
        entity.FirstLearnedAt = card.FirstLearnedAt;
        entity.FirstLearnedPlanId = card.FirstLearnedPlanId;
        entity.LearningStep = card.LearningStep;
        entity.Version = card.Version + 1;
        _dbContext.Entry(entity).Property(e => e.Version).OriginalValue = card.Version;
        card.Version = entity.Version;
    }

    public async Task<List<MemoryCardModel>> GetDueAsync(Guid studentId, DateTimeOffset now, IReadOnlyCollection<CardKey>? keys = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.MemoryCards.AsNoTracking().Where(e => e.StudentId == studentId && e.DueAt <= now);
        if (keys is { Count: > 0 })
        {


            var tokens = keys.Select(k => k.WordKey + "\n" + k.MeaningKey).ToList();
            query = query.Where(e => tokens.Contains(e.WordKey + "\n" + e.MeaningKey));
        }

        var entities = await query
            .OrderBy(e => e.DueAt)
            .ThenBy(e => e.WordKey)
            .ThenBy(e => e.MeaningKey)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToModel).ToList();
    }

    public async Task<List<MemoryCardModel>> GetDuePageAsync(Guid studentId, DateTimeOffset now, MemoryCardCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.MemoryCards.AsNoTracking().Where(card => card.StudentId == studentId && card.DueAt <= now);
        if (after is not null)
        {
            query = query.Where(card => card.DueAt > after.DueAt
                || (card.DueAt == after.DueAt && (string.Compare(card.WordKey, after.WordKey) > 0
                    || (card.WordKey == after.WordKey && string.Compare(card.MeaningKey, after.MeaningKey) > 0))));
        }
        var entities = await query.OrderBy(card => card.DueAt).ThenBy(card => card.WordKey).ThenBy(card => card.MeaningKey)
            .Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToModel).ToList();
    }

    public async Task<HashSet<CardKey>> FindExistingKeysAsync(Guid studentId, IReadOnlyCollection<CardKey> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var tokens = keys.Select(k => k.WordKey + "\n" + k.MeaningKey).ToList();
        var entities = await _dbContext.MemoryCards.AsNoTracking()
            .Where(e => e.StudentId == studentId && tokens.Contains(e.WordKey + "\n" + e.MeaningKey))
            .Select(e => new { e.WordKey, e.MeaningKey })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(e => new CardKey(e.WordKey, e.MeaningKey)).ToHashSet();
    }

    public Task<int> CountDueAsync(Guid studentId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return _dbContext.MemoryCards.AsNoTracking()
            .CountAsync(e => e.StudentId == studentId && e.DueAt <= now, cancellationToken);
    }

    private static MemoryCardEntity ToEntity(MemoryCardModel model) => new()
    {
        StudentId = model.StudentId,
        WordKey = model.WordKey,
        MeaningKey = model.MeaningKey,
        State = (int)model.State,
        Stability = model.Stability,
        Difficulty = model.Difficulty,
        DueAt = model.DueAt,
        Reps = model.Reps,
        Lapses = model.Lapses,
        LastReviewedAt = model.LastReviewedAt!.Value,
        FirstLearnedAt = model.FirstLearnedAt,
        FirstLearnedPlanId = model.FirstLearnedPlanId,
        LearningStep = model.LearningStep,
        Version = model.Version,
    };

    private static MemoryCardModel ToModel(MemoryCardEntity entity) => new()
    {
        StudentId = entity.StudentId,
        WordKey = entity.WordKey,
        MeaningKey = entity.MeaningKey,
        State = (MemoryCardState)entity.State,
        Stability = entity.Stability,
        Difficulty = entity.Difficulty,
        DueAt = entity.DueAt,
        Reps = entity.Reps,
        Lapses = entity.Lapses,
        LastReviewedAt = entity.LastReviewedAt,
        FirstLearnedAt = entity.FirstLearnedAt,
        FirstLearnedPlanId = entity.FirstLearnedPlanId,
        LearningStep = entity.LearningStep,
        Version = entity.Version,
    };
}
