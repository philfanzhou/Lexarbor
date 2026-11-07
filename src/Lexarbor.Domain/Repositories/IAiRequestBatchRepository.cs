using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public sealed record AiBatchMember(string Id, EnrichmentExecutionGuard? Guard = null, MeaningSubmissionReviewLease? Review = null);
public sealed record AiBatchResult(string Id, EnrichmentPlan? Plan = null, MeaningSubmissionAiReview? Review = null,
    string? ErrorCode = null);
public sealed record AiBatchReceipt(string Id, IReadOnlyList<AiBatchMember> Members);
public sealed record AiBatchStatistics(int Requests, int Participations, int Completed, int Unknown);

/// <summary>Physical requests and their individually guarded members; never raw supplier content.</summary>
public interface IAiRequestBatchRepository
{
    Task<int> LimitAsync(string kind, string configurationVersion, int initial, int maximum, CancellationToken token);
    Task<AiBatchReceipt?> BeginAsync(string kind, string configurationVersion, IReadOnlyList<AiBatchMember> members,
        long now, CancellationToken token);
    Task CompleteAsync(AiBatchReceipt batch, IReadOnlyList<AiBatchResult> results, string outcome, string? errorCode,
        int nextSize, long now, long? retryAt, CancellationToken token,
        EnrichmentServiceAdmission? admission = null, EnrichmentServicePolicy? policy = null);
    Task<AiBatchStatistics> StatisticsAsync(CancellationToken token);
}
