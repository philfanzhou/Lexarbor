using Lexarbor.Database.Entities;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed record BusinessApiKeyMetadata(string Id, string CallerId, bool Catalog, bool MeaningSubmissions,
    long CreatedAtUnixMs, long? RevokedAtUnixMs);

public sealed record BusinessApiCaller(string CallerId, bool Catalog, bool MeaningSubmissions);

public sealed class BusinessApiKeyRepository(VocabularyDbContext context, IUnitOfWork unitOfWork)
{
    public Task<BusinessApiCaller?> FindActiveCallerAsync(string secretHash, CancellationToken token) => context.BusinessApiKeys
        .AsNoTracking().Where(key => key.SecretHash == secretHash && key.RevokedAtUnixMs == null)
        .Select(key => new BusinessApiCaller(key.CallerId, key.Catalog, key.MeaningSubmissions)).SingleOrDefaultAsync(token);

    public Task<BusinessApiKeyMetadata[]> ListAsync(CancellationToken token) => context.BusinessApiKeys.AsNoTracking()
        .OrderByDescending(key => key.CreatedAtUnixMs).ThenBy(key => key.Id)
        .Select(key => new BusinessApiKeyMetadata(key.Id, key.CallerId, key.Catalog, key.MeaningSubmissions,
            key.CreatedAtUnixMs, key.RevokedAtUnixMs)).ToArrayAsync(token);

    public Task<bool> TryAddAsync(BusinessApiKeyMetadata key, string secretHash, CancellationToken token) => unitOfWork.ExecuteInTransactionAsync(async () =>
    {
        // Submissions share one catalog-wide workflow and historically trusted
        // one caller. Overlapping keys are allowed for that caller's rotation.
        if (key.MeaningSubmissions && await context.BusinessApiKeys.AnyAsync(existing =>
            existing.MeaningSubmissions && existing.RevokedAtUnixMs == null && existing.CallerId != key.CallerId, token))
            return false;

        context.BusinessApiKeys.Add(new BusinessApiKeyEntity
        {
            Id = key.Id,
            SecretHash = secretHash,
            CallerId = key.CallerId,
            Catalog = key.Catalog,
            MeaningSubmissions = key.MeaningSubmissions,
            CreatedAtUnixMs = key.CreatedAtUnixMs
        });
        await unitOfWork.SaveChangesAsync(token);
        return true;
    }, token);

    public Task<bool> RevokeAsync(string id, long now, CancellationToken token) => unitOfWork.ExecuteInTransactionAsync(async () =>
    {
        var keys = context.BusinessApiKeys.Where(key => key.Id == id);
        var changed = await keys.Where(key => key.RevokedAtUnixMs == null)
            .ExecuteUpdateAsync(set => set.SetProperty(key => key.RevokedAtUnixMs,
                key => Math.Max(now, key.CreatedAtUnixMs)), token);
        return changed == 1 || await keys.AnyAsync(token);
    }, token);
}
