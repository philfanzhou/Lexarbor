namespace Lexarbor.Domain.Models;

public enum AudioLocale { EnGb, EnUs }
public enum AudioContentStatus { Missing, Ready, Stale }
public enum AudioBindingSource { Default, Meaning }
public enum AudioUploadState { Reserved, Transferring, Succeeded, Abandoned, CleanupClaimed, CleanupObserved }

public static class AudioLocales
{
    public static string ToCode(this AudioLocale locale) => locale switch
    {
        AudioLocale.EnGb => "en-GB",
        AudioLocale.EnUs => "en-US",
        _ => throw new ArgumentOutOfRangeException(nameof(locale))
    };

    public static AudioLocale FromCode(string code) => code switch
    {
        "en-GB" => AudioLocale.EnGb,
        "en-US" => AudioLocale.EnUs,
        _ => throw new ArgumentException("Audio locale is invalid.", nameof(code))
    };
}

// Internal models: object locations never belong in public HTTP responses.
public sealed record AudioObjectLocation(string Bucket, string Key);
public sealed record AudioAsset(string Id, AudioObjectLocation Location, string Sha256,
    long ByteLength, string ContentType, long CreatedAtUnixMs, long? FirstPublishedAtUnixMs);
public sealed record AudioBinding(string WordId, string? MeaningId, AudioLocale Locale,
    AudioAsset Asset, string SourceRevision);
public sealed record AudioWordInput(string WordId, string Word, string? PhoneticUk, string? PhoneticUs);
public sealed record AudioMeaningInput(string MeaningId, string? PartOfSpeech, string Meaning,
    string? PhoneticUk = null, string? PhoneticUs = null);
public sealed record AudioReadSnapshot(AudioWordInput Word, AudioMeaningInput? Meaning,
    IReadOnlyList<AudioBinding> Bindings);
public sealed record AudioGenerationInput(string Word, AudioLocale Locale, string? Phonetic,
    bool IsMeaning, string? PartOfSpeech, string? Meaning);
public sealed record AudioSelection(AudioLocale Locale, AudioGenerationInput Input, string SourceRevision,
    AudioBinding? OwnBinding, AudioBinding? EffectiveBinding, AudioBindingSource? Source,
    AudioContentStatus Status);
public sealed record AudioObjectInfo(long ByteLength, string ContentType);
public sealed record AudioPlaybackLink(string Url, DateTimeOffset ExpiresAt);

// Request receipts are separate from retryable object cleanup progress. The
// conditional persistence boundary preserves them independently of cleanup.
public sealed record AudioUploadReceipt(int StatusCode, string? AssetId, string? SourceRevision,
    string? ErrorCode, long CompletedAtUnixMs);
public sealed record AudioLease(string? Owner, long Fence, long? ExpiresAtUnixMs);
public sealed record AudioCleanupProgress(AudioLease Lease, AudioUploadState? ReturnState,
    int Attempts, long? NextAttemptAtUnixMs, string? LastErrorCode, long? ObservedAtUnixMs);
public sealed record AudioUploadOperation(string IdempotencyKey, string RequestFingerprint,
    string WordId, string? MeaningId, AudioLocale Locale, string SourceRevision,
    string? ExpectedAssetId, string AssetId, string? Producer, string? Voice,
    AudioUploadState State, long CreatedAtUnixMs, long UpdatedAtUnixMs, AudioLease UploadLease,
    AudioUploadReceipt? Receipt, AudioCleanupProgress Cleanup);

public sealed record AudioTargetQuery(string? BookId, AudioLocale? Locale, AudioContentStatus? Status, int Page, int Size);
public sealed record AudioTarget(string WordId, string? MeaningId, AudioSelection Selection);
public sealed record AudioTargetPage(IReadOnlyList<AudioTarget> Items, int TotalCount, int TotalPage);
