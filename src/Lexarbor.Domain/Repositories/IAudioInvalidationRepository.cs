using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IAudioInvalidationRepository
{
    // Caller must own the actual serialized writer transaction on this context.
    // Call while the old specific binding still exists, then unbind in that same
    // transaction. This method neither commits nor performs object-store IO.
    Task<bool> AuthorizeInTransactionAsync(AudioInvalidationAuthorization authorization, long now, CancellationToken token);
    Task<AudioInvalidation?> GetAsync(string assetId, CancellationToken token);
    Task<IReadOnlyList<AudioInvalidation>> FindRecoverableAsync(long now, int limit, CancellationToken token);
    Task<AudioInvalidation?> ClaimAsync(string assetId, string owner, long now, long expiresAt, long referenceRetryAt, CancellationToken token);
    Task<bool> ExpireAsync(string assetId, long now, long nextAttemptAt, CancellationToken token);
    Task<bool> ReleaseAsync(string assetId, AudioLease lease, long now, long nextAttemptAt, CancellationToken token);
    Task<bool> ObserveAsync(string assetId, AudioLease lease, long now, long nextAttemptAt, CancellationToken token);
}
