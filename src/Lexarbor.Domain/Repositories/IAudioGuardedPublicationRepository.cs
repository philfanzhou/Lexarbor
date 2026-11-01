using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

// Background publication requires BOTH execution and upload leases. Validation
// belongs inside the binding writer transaction, never in a preceding read.
public interface IAudioGuardedPublicationRepository
{
    Task<AudioUploadOperation?> CompleteGuardedAsync(string key, AudioLease lease, EnrichmentExecutionGuard guard,
        long now, CancellationToken token);
}
