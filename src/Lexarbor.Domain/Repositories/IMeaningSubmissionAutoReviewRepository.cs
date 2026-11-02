using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public sealed record MeaningSubmissionReviewLease(string SubmissionId, string AttemptId, string Owner, long Epoch);
public sealed record MeaningSubmissionReviewBacklog(int PendingCount, string? LastFailureCode,
    DateTimeOffset? LastFailureAt, DateTimeOffset? NextAttemptAt);
public sealed record MeaningSubmissionAutomaticReviewStatus(bool Enabled, bool Paused, string? PauseCode,
    int PendingCount, string? LastFailureCode, DateTimeOffset? LastFailureAt, DateTimeOffset? NextAttemptAt);

/// <summary>Short persistent scheduling operations; provider IO must remain outside them.</summary>
public interface IMeaningSubmissionAutoReviewRepository
{
    Task<MeaningSubmissionReviewLease?> ClaimAsync(string owner, TimeSpan leaseDuration, CancellationToken token);
    /// <summary>Read the real lease inside the submission result's writer/context.</summary>
    Task<bool> IsCurrentAsync(MeaningSubmissionReviewLease lease, CancellationToken token);
    Task<MeaningSubmissionAiReview?> ReadValidatedAsync(MeaningSubmissionReviewLease lease, CancellationToken token);
    /// <summary>Durably retain bounded structured input before retryable application; never raw supplier JSON.</summary>
    Task StageValidatedAsync(MeaningSubmissionReviewLease lease, MeaningSubmissionAiReview review, CancellationToken token);
    /// <summary>Only this owner/attempt/epoch may release; failures retry forever with bounded exponential delay.</summary>
    Task FinishAsync(MeaningSubmissionReviewLease lease, string? failureCode, TimeSpan? retryAfter, CancellationToken token);
    Task<MeaningSubmissionReviewBacklog> ReadBacklogAsync(CancellationToken token);
}

public interface IMeaningSubmissionAutomaticReviewStatusProvider
{
    Task<MeaningSubmissionAutomaticReviewStatus> ReadAsync(CancellationToken token);
}

/// <summary>A lossy wakeup after a successful submission commit. Pending rows are authoritative.</summary>
public interface IMeaningSubmissionChangeNotifier { void Notify(); }
