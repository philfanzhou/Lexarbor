using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public sealed record EnrichmentExecutionSnapshot(PronunciationPreparationRequest Request, string? OwnAssetId);
public sealed record EnrichmentFieldCommit(EnrichmentExecutionGuard Guard, EnrichmentPlan Plan,
    IReadOnlyList<string> ConflictingMeaningIds);

public interface IEnrichmentExecutionRepository
{
    Task<EnrichmentExecutionSnapshot?> ReadAsync(EnrichmentTargetKey target, CancellationToken token);
    // Owns a short writer transaction. Rechecks guard, captured input and OWN
    // binding; applies only missing default fields and advances the same round.
    Task<EnrichmentFieldCommit?> CommitFieldsAsync(EnrichmentExecutionGuard guard, EnrichmentPlan plan,
        long now, CancellationToken token);
}
