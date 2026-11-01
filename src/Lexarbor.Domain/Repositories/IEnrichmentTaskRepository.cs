using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

/// <summary>
/// Network-free queue persistence. Times are UTC Unix milliseconds; expiry is
/// exclusive. ConfigurationVersion is the relevant opaque durable version, never
/// an HTTP ETag or a credential fingerprint. Each step/accent has its own budget.
/// </summary>
public interface IEnrichmentTaskRepository
{
    Task<EnrichmentTask?> GetAsync(string targetId, CancellationToken cancellationToken);
    Task<EnrichmentInputVersion?> ReadInputAsync(EnrichmentTargetKey target, CancellationToken cancellationToken);
    Task<EnrichmentTask?> EnsureAsync(EnrichmentTargetKey target, string configurationVersion, long now,
        CancellationToken cancellationToken, int failureBudget = 5, string? retryKey = null);
    Task<EnrichmentExecutionGuard?> ClaimAsync(string targetId, string owner, long now, long expiresAt,
        CancellationToken cancellationToken);
    Task<bool> RenewAsync(EnrichmentExecutionGuard guard, long now, long expiresAt, CancellationToken cancellationToken);
    Task<bool> SavePlanAsync(EnrichmentExecutionGuard guard, EnrichmentPlan plan, long now, CancellationToken cancellationToken);
    // Call immediately before dispatch, once per request ID. Started means its
    // remote outcome may be unknown after a crash; reuse its upload key to read
    // the permanent upload receipt before making a new generation attempt.
    Task<bool> BeginCallAsync(EnrichmentExecutionGuard guard, string callId, string service,
        string? uploadIdempotencyKey, long now, CancellationToken cancellationToken);
    Task<bool> FinishCallAsync(EnrichmentExecutionGuard guard, string callId, EnrichmentCallResult result,
        string? errorCode, long now, long? nextAttemptAt, CancellationToken cancellationToken);
    // Joins the actual scheduler writer so shared service outcomes and item
    // throttling/failure accounting cannot be torn apart by a crash.
    Task<bool> FinishCallInTransactionAsync(EnrichmentExecutionGuard guard, string callId, EnrichmentCallResult result,
        string? errorCode, long now, long? nextAttemptAt, CancellationToken cancellationToken);
    Task<bool> WaitAsync(EnrichmentExecutionGuard guard, EnrichmentWaitReason reason, long now,
        long nextAttemptAt, CancellationToken cancellationToken);
    Task<bool> RecoverPublishedAsync(string targetId, string uploadIdempotencyKey, long now, CancellationToken cancellationToken);
    Task<bool> StopAsync(EnrichmentExecutionGuard guard, string errorCode, long now, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(EnrichmentExecutionGuard guard, long now, CancellationToken cancellationToken);

    // These methods MUST join the caller's actual serialized writer transaction,
    // on the same scoped context as the field/binding mutation. Standalone reads
    // cannot authorize a later write. Invalidation advances an independent epoch
    // even for A -> B -> A; callers invoke it only on an actual relevant change.
    Task<bool> ValidateInTransactionAsync(EnrichmentExecutionGuard guard, long now, CancellationToken cancellationToken);
    Task<EnrichmentExecutionGuard?> AdvanceInputInTransactionAsync(EnrichmentExecutionGuard guard, long now,
        CancellationToken cancellationToken);
    Task<EnrichmentTask?> InvalidateInTransactionAsync(EnrichmentTargetKey target, string configurationVersion,
        long now, CancellationToken cancellationToken, int failureBudget = 5);
}
