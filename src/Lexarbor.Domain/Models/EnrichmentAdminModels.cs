namespace Lexarbor.Domain.Models;

// Server-only captured inputs. Never serialize this configuration into a response.
public sealed record EnrichmentAdminConfiguration(bool FieldsEnabled, bool AudioEnabled, bool StorageEnabled,
    bool CanPrepare, bool CanTtsUk, bool CanTtsUs, int FailureBudget, string DefaultPreparationVersion,
    string SpecificPreparationVersion, string UkAudioVersion, string UsAudioVersion,
    string TextServiceId, string TextServiceVersion, string TtsServiceId, string TtsServiceVersion)
{
    public string Version(EnrichmentTargetKey key) => key.Step == EnrichmentStep.Audio
        ? key.Locale == AudioLocale.EnGb ? UkAudioVersion : UsAudioVersion
        : key.MeaningId == null ? DefaultPreparationVersion : SpecificPreparationVersion;
}
public sealed record EnrichmentAdminFilter(string? WordId = null, string? MeaningId = null, string? Locale = null,
    string? State = null, string? Step = null, int Page = 1, int Size = 25);
public sealed record EnrichmentAdminPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int Size);
public sealed record EnrichmentAdminRound(int Number, string State, int RequestCount, int ConsumedFailures,
    int FailureBudget, string? WaitReason, string? ErrorCode, long? NextAttemptAtUnixMs,
    long CreatedAtUnixMs, long UpdatedAtUnixMs);
public sealed record EnrichmentAdminCleanup(int Waiting, int Running, int Observed, int Attempts,
    long? NextAttemptAtUnixMs, string? ErrorCode);
public sealed record EnrichmentAdminTask(string Id, string WordId, string? Word, string? MeaningId, string? Meaning,
    string Locale, string Step, bool Applicable, bool Missing, bool InheritsDefault, string State,
    string? WaitReason, EnrichmentAdminRound Current, EnrichmentAdminCleanup Cleanup);
public sealed record EnrichmentAdminCall(string Id, int Round, string Service, string Result, string? ErrorCode,
    long StartedAtUnixMs, long? FinishedAtUnixMs);
public sealed record EnrichmentAdminWait(int Round, string Reason, string? ErrorCode, long RecordedAtUnixMs, long NextAttemptAtUnixMs);
public sealed record EnrichmentAdminCleanupHistory(long Epoch, string State, int Attempts, long NextAttemptAtUnixMs,
    string? ErrorCode, long CreatedAtUnixMs, long UpdatedAtUnixMs);
public sealed record EnrichmentAdminDetail(EnrichmentAdminTask Task, EnrichmentAdminPage<EnrichmentAdminRound> Rounds,
    EnrichmentAdminPage<EnrichmentAdminCall> Calls, EnrichmentAdminPage<EnrichmentAdminWait> Waits,
    EnrichmentAdminPage<EnrichmentAdminCleanupHistory> Cleanup);
public sealed record EnrichmentAdminCount(string State, string? WaitReason, int Count);
public sealed record EnrichmentAdminService(string Service, bool Paused, bool Blocked, int ConsecutiveFailures,
    int ThrottleSeconds, long? NextAttemptAtUnixMs);
public sealed record EnrichmentAdminSummary(int ApplicableTargets, int CompletedTargets, int MissingTargets,
    int UnqueuedTargets, int PersistedTasks, IReadOnlyList<EnrichmentAdminCount> Targets,
    IReadOnlyList<EnrichmentAdminService> Services);
public enum EnrichmentRetryStatus { Started, Replayed, NotFound, Conflict }
public sealed record EnrichmentRetryResult(EnrichmentRetryStatus Status, string TargetId, int? Round = null);
