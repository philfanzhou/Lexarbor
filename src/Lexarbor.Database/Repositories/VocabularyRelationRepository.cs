using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyRelationRepository(VocabularyDbContext context) : IVocabularyRelationRepository
{
    public Task<bool> WordExistsAsync(string id, CancellationToken cancellationToken) =>
        context.Vocabularies.AnyAsync(word => word.Id == id, cancellationToken);

    public async Task<VocabularyRelationModel?> GetAsync(string id, CancellationToken cancellationToken) =>
        Model(await context.VocabularyRelations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken));

    public async Task<VocabularyRelationModel?> FindAsync(string sourceWordId, string targetWordId, string type,
        CancellationToken cancellationToken) => Model(await context.VocabularyRelations.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SourceWordId == sourceWordId && r.TargetWordId == targetWordId && r.Type == type, cancellationToken));

    public async Task<VocabularyRelationPage> ListAsync(string? wordId, VocabularyRelationQuery query, CancellationToken cancellationToken)
    {
        // Count and page share a bounded read transaction; no write lock is acquired.
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
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

    public async Task AddAsync(VocabularyRelationModel relation, CancellationToken cancellationToken)
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

    public async Task<bool> ReplaceAsync(VocabularyRelationModel relation, long expectedVersion, CancellationToken cancellationToken) =>
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

    private static VocabularyRelationModel? Model(VocabularyRelationEntity? r) => r == null ? null : new(
        r.Id, r.SourceWordId, r.TargetWordId, r.Type, r.UsageNote, r.Status, r.ReviewReason, r.OriginKind,
        r.OriginReference, r.Version, r.CreatedAtUnixMs, r.UpdatedAtUnixMs, r.LastConfirmedAtUnixMs);
}
