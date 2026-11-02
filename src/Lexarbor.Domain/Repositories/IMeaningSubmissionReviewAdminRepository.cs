using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IMeaningSubmissionReviewAdminRepository
{
    /// <summary>The count and page share a single deferred read snapshot; reads have no side effects.</summary>
    Task<(List<MeaningSubmissionReviewEntry> Items, int TotalCount)> PageAsync(
        MeaningSubmissionReviewQuery query, CancellationToken cancellationToken = default);

    /// <summary>Append one independent fact per permanent record, returning its original first-check time on replay.</summary>
    Task<MeaningSubmissionReviewCheck> CheckAsync(string recordId, CancellationToken cancellationToken = default);
}
