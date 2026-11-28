using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyRelationRepository(VocabularyDbContext context) : IVocabularyRelationRepository
{
    public Task<bool> WordExistsAsync(string id, CancellationToken cancellationToken) =>
        context.Vocabularies.AnyAsync(word => word.Id == id, cancellationToken);

    public async Task<VocabularyRelation?> GetAsync(string id, CancellationToken cancellationToken) =>
        Model(await context.VocabularyRelations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken));

    public async Task<VocabularyRelation?> FindAsync(string sourceWordId, string targetWordId, string type,
        CancellationToken cancellationToken) => Model(await context.VocabularyRelations.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SourceWordId == sourceWordId && r.TargetWordId == targetWordId && r.Type == type, cancellationToken));

    public async Task<VocabularyRelationPage> ListAsync(string? wordId, VocabularyRelationQuery query, CancellationToken cancellationToken)
    {
        // Count and page share a bounded read transaction; no write lock is acquired.
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = await ReadSnapshot.BeginAsync(context, cancellationToken);
            var rows = context.VocabularyRelations.AsNoTracking().AsQueryable();
            if (wordId != null)
                rows = query.Direction switch
                {
                    "outgoing" => rows.Where(r => r.SourceWordId == wordId),
                    "incoming" => rows.Where(r => r.TargetWordId == wordId),
                    _ => rows.Where(r => r.SourceWordId == wordId || r.TargetWordId == wordId)
                };
            if (query.Type != null) rows = rows.Where(r => r.Type == query.Type);
            if (query.Status != null) rows = rows.Where(r => r.Status == query.Status);
            var total = await rows.CountAsync(cancellationToken);
            var items = await rows.OrderBy(r => r.UpdatedAtUnixMs).ThenBy(r => r.Id)
                .Skip((query.Page - 1) * query.Size).Take(query.Size).ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(items.Select(r => Model(r)!).ToArray(), total, (int)Math.Ceiling(total / (double)query.Size));
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    public async Task AddAsync(VocabularyRelation relation, CancellationToken cancellationToken)
    {
        await context.VocabularyRelations.AddAsync(new VocabularyRelationEntity
        {
            Id = relation.Id,
            SourceWordId = relation.SourceWordId,
            TargetWordId = relation.TargetWordId,
            Type = relation.Type,
            UsageNote = relation.UsageNote,
            Status = relation.Status,
            ReviewReason = relation.ReviewReason,
            OriginKind = relation.OriginKind,
            OriginReference = relation.OriginReference,
            Version = relation.Version,
            CreatedAtUnixMs = relation.CreatedAtUnixMs,
            UpdatedAtUnixMs = relation.UpdatedAtUnixMs,
            LastConfirmedAtUnixMs = relation.LastConfirmedAtUnixMs
        }, cancellationToken);
    }

    public async Task<bool> ReplaceAsync(VocabularyRelation relation, long expectedVersion, CancellationToken cancellationToken) =>
        await context.VocabularyRelations.Where(r => r.Id == relation.Id && r.Version == expectedVersion)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.SourceWordId, relation.SourceWordId)
                .SetProperty(r => r.TargetWordId, relation.TargetWordId).SetProperty(r => r.Type, relation.Type)
                .SetProperty(r => r.UsageNote, relation.UsageNote).SetProperty(r => r.Status, relation.Status)
                .SetProperty(r => r.ReviewReason, relation.ReviewReason).SetProperty(r => r.Version, relation.Version)
                .SetProperty(r => r.UpdatedAtUnixMs, relation.UpdatedAtUnixMs)
                .SetProperty(r => r.LastConfirmedAtUnixMs, relation.LastConfirmedAtUnixMs), cancellationToken) == 1;

    public async Task<bool> DeleteAsync(string id, long expectedVersion, CancellationToken cancellationToken) =>
        await context.VocabularyRelations.Where(r => r.Id == id && r.Version == expectedVersion)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    public async Task<IReadOnlyList<VocabularyRelationMeaningPair>> GetMeaningPairsAsync(string relationId, CancellationToken cancellationToken) =>
        await context.VocabularyRelationMeanings.AsNoTracking().Where(pair => pair.RelationId == relationId)
            .OrderBy(pair => pair.SourceMeaningId).ThenBy(pair => pair.TargetMeaningId)
            .Select(pair => new VocabularyRelationMeaningPair(pair.SourceMeaningId, pair.TargetMeaningId)).ToArrayAsync(cancellationToken);

    public async Task ReplaceMeaningPairsAsync(VocabularyRelation relation, IReadOnlyList<VocabularyRelationMeaningPair> pairs,
        CancellationToken cancellationToken)
    {
        var ids = pairs.SelectMany(pair => new[] { pair.SourceMeaningId, pair.TargetMeaningId }).Distinct(StringComparer.Ordinal).ToArray();
        var owners = await context.VocabularyMeanings.AsNoTracking().Where(meaning => EF.Parameter(ids).Contains(meaning.Id))
            .ToDictionaryAsync(meaning => meaning.Id, meaning => meaning.VocabularyId, cancellationToken);
        if (pairs.Any(pair => owners.GetValueOrDefault(pair.SourceMeaningId) != relation.SourceWordId
            || owners.GetValueOrDefault(pair.TargetMeaningId) != relation.TargetWordId))
            throw new DomainValidationException("Each meaning must belong to its relation endpoint.");

        await context.VocabularyRelationMeanings.Where(pair => pair.RelationId == relation.Id).ExecuteDeleteAsync(cancellationToken);
        foreach (var pair in pairs)
            await AddMeaningPairAsync(relation.Id, pair, cancellationToken);
    }

    public async Task<bool> AddMeaningPairAsync(string relationId, VocabularyRelationMeaningPair pair, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO vocabulary_relation_meaning (relation_id, source_meaning_id, target_meaning_id)
            VALUES ({relationId}, {pair.SourceMeaningId}, {pair.TargetMeaningId})
            ON CONFLICT (relation_id, source_meaning_id, target_meaning_id) DO NOTHING
            """, cancellationToken) == 1;

    private static VocabularyRelation? Model(VocabularyRelationEntity? r) => r == null ? null : new(
        r.Id, r.SourceWordId, r.TargetWordId, r.Type, r.UsageNote, r.Status, r.ReviewReason, r.OriginKind,
        r.OriginReference, r.Version, r.CreatedAtUnixMs, r.UpdatedAtUnixMs, r.LastConfirmedAtUnixMs);
}
