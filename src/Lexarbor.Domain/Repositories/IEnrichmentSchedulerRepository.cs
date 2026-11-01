using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

// Best-effort wakeups only. Durable discovery remains authoritative.
public interface IEnrichmentChangeNotifier { void Notify(IReadOnlyList<string> wordIds); }

public sealed record EnrichmentServicePolicy(int FailureThreshold, int PauseSeconds, int MaxPauseSeconds,
    int ThrottleSeconds, int MaxThrottleSeconds);
public sealed record EnrichmentServiceAdmission(string ServiceId, string ConfigurationVersion, string? LeaseId,
    bool Probe, EnrichmentWaitReason? WaitReason, long NextAttemptAtUnixMs);
public sealed record EnrichmentServiceStatus(string Id, string ConfigurationVersion, int ConsecutiveFailures,
    int ThrottleSeconds, bool Paused, bool Blocked, int PauseSeconds, long NextAttemptAtUnixMs,
    string? LeaseId, long? LeaseExpiresAtUnixMs);
public sealed record EnrichmentServiceProbe(string Id, string ServiceId, string Result, bool RequestMade,
    long StartedAtUnixMs, long? FinishedAtUnixMs);
public sealed record EnrichmentCallCompletion(EnrichmentExecutionGuard Guard, string CallId, EnrichmentCallResult Result,
    string ErrorCode, long? NextAttemptAtUnixMs);

public interface IEnrichmentSchedulerRepository
{
    Task<IReadOnlyList<EnrichmentTargetKey>> DiscoverAsync(int batchSize, IReadOnlyList<string> recentWordIds, CancellationToken token);
    Task<IReadOnlyList<string>> DueAsync(int limit, long now, CancellationToken token);
    Task<EnrichmentServiceAdmission> EnterServiceAsync(string serviceId, string configurationVersion,
        bool probe, long now, long expiresAt, CancellationToken token);
    Task<long> FinishServiceAsync(EnrichmentServiceAdmission admission, string outcome, string? errorCode,
        bool requestMade, long now, long? retryAfterAt, EnrichmentServicePolicy policy, CancellationToken token,
        EnrichmentCallCompletion? completion = null);
    Task<EnrichmentServiceStatus?> ServiceAsync(string serviceId, CancellationToken token);
    Task<IReadOnlyList<EnrichmentServiceProbe>> ProbesAsync(string serviceId, CancellationToken token);
    Task<EnrichmentPlan?> ProbePlanAsync(AudioLocale locale, string textConfigurationVersion, CancellationToken token);
}
