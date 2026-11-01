namespace Lexarbor.Domain.Models;

public enum AudioInvalidationState { Waiting, Claimed, Observed }

// Internal deletion authority: never returned by public HTTP contracts.
public sealed record AudioInvalidationAuthorization(string WordId, string MeaningId, AudioLocale Locale,
    string AssetId, long Epoch);
public sealed record AudioInvalidation(string AssetId, AudioObjectLocation Location, string WordId,
    string MeaningId, AudioLocale Locale, long AuthorizationEpoch, AudioInvalidationState State,
    AudioLease Lease, int Attempts, long NextAttemptAtUnixMs, long? DeleteStartedAtUnixMs,
    long? ObservedAtUnixMs, string? ErrorCode);
