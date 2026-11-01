namespace Lexarbor.Domain.Models;

public enum EnrichmentStep { Prepare, Fields, Audio }
public enum EnrichmentState { Pending, Running, Waiting, Succeeded, Failed, Superseded, Cancelled }
public enum EnrichmentCallResult { Started, Succeeded, Throttled, TransientFailure, TerminalFailure }
public enum EnrichmentWaitReason { Disabled, ConfigurationMissing, StorageDisabled, ServicePaused, Throttled, Retry, LeaseExpired }

// Internal queue models. Persist only bounded plans and safe codes, never raw
// provider responses, credentials, object locations or signed URLs.
public sealed record EnrichmentTargetKey(string WordId, string? MeaningId, AudioLocale Locale, EnrichmentStep Step);
public sealed record EnrichmentInputVersion(string Revision, string SourceRevision, string? OwnAssetId);
public sealed record EnrichmentMeaningPartOfSpeech(string MeaningId, string PartOfSpeech);
public sealed record EnrichmentPlan(string SpokenText, string? PronunciationHint, string? Phonetic,
    IReadOnlyList<EnrichmentMeaningPartOfSpeech>? PartsOfSpeech, string? InferredEntryKind = null,
    EnrichmentPreparationMetadata? Preparation = null);
// Optional v1 JSON extension. Old content-only plans cannot authorize speech.
public sealed record EnrichmentPreparationMetadata(AudioGenerationInput Input, EnrichmentInputVersion InputVersion,
    string ConfigurationVersion, string EntryKind, string ClassificationBasis, string? RequiredPhonetic,
    string? SpeechPhonetic, PronunciationControl Control, string? OwnAssetId,
    IReadOnlyList<string>? ConflictingMeaningIds = null);
public sealed record EnrichmentExecutionGuard(string TargetId, int Round, long Epoch, string Owner, long Fence,
    EnrichmentInputVersion Input);
public sealed record EnrichmentRound(int Number, EnrichmentState State, EnrichmentInputVersion Input,
    string ConfigurationVersion, int FailureBudget, int RequestCount, int ConsumedFailures,
    EnrichmentPlan? Plan, EnrichmentWaitReason? WaitReason, string? ErrorCode, long? NextAttemptAtUnixMs,
    string? LeaseOwner, long LeaseFence, long? LeaseExpiresAtUnixMs, long CreatedAtUnixMs, long UpdatedAtUnixMs);
public sealed record EnrichmentCall(string Id, int Round, string Service, EnrichmentCallResult Result,
    string? UploadIdempotencyKey, string? ErrorCode, long StartedAtUnixMs, long? FinishedAtUnixMs);
public sealed record EnrichmentWait(int Round, EnrichmentWaitReason Reason, string? ErrorCode,
    long RecordedAtUnixMs, long NextAttemptAtUnixMs);
public sealed record EnrichmentTask(string Id, EnrichmentTargetKey Target, long Epoch, bool Deleted,
    int CurrentRound, IReadOnlyList<EnrichmentRound> Rounds, IReadOnlyList<EnrichmentCall> Calls, IReadOnlyList<EnrichmentWait> Waits);
