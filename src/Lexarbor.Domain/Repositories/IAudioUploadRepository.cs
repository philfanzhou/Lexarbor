using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public enum AudioReservationDisposition { Created, Replay, InProgress, Conflict, NotFound }
public enum AudioUnbindDisposition { Removed, Conflict, NotFound }
public sealed record AudioUploadRequest(string IdempotencyKey, string RequestFingerprint,
    string WordId, string? MeaningId, AudioLocale Locale, string SourceRevision, string? ExpectedAssetId,
    string Sha256, long ByteLength, string Bucket, string? Producer = null, string? Voice = null, string KeyPrefix = "audio/");
public sealed record AudioStoredUpload(AudioUploadOperation Operation, AudioAsset Asset);
public sealed record AudioReservation(AudioReservationDisposition Disposition, AudioStoredUpload? Upload);

/// <summary>
/// Short, atomic database operations only. Call Put/Remove/Stat between calls,
/// never inside an enclosing UnitOfWork transaction. Times are UTC Unix milliseconds.
/// Lease owner and fence must both match; expiry is exclusive.
/// </summary>
public interface IAudioUploadRepository
{
    Task<AudioStoredUpload?> GetAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task<AudioReservation> ReserveAsync(AudioUploadRequest request, string owner, long now, long leaseExpiresAt,
        CancellationToken cancellationToken);
    Task<bool> BeginTransferAsync(string key, AudioLease lease, long now, CancellationToken cancellationToken);
    Task<bool> RenewAsync(string key, AudioLease lease, long now, long leaseExpiresAt, CancellationToken cancellationToken);
    Task<AudioUploadOperation?> CompleteAsync(string key, AudioLease lease, long now, CancellationToken cancellationToken);
    Task<bool> AbandonAsync(string key, AudioLease lease, long now, int statusCode, string errorCode,
        CancellationToken cancellationToken);
    Task<bool> ExpireAsync(string key, long now, CancellationToken cancellationToken);
    Task<AudioStoredUpload?> ClaimCleanupAsync(string key, string owner, long now, long leaseExpiresAt,
        CancellationToken cancellationToken);
    Task<bool> ReleaseCleanupAsync(string key, AudioLease lease, long now, long nextAttemptAt, string errorCode,
        CancellationToken cancellationToken);
    Task<bool> ObserveCleanupAsync(string key, AudioLease lease, long now, long nextAttemptAt,
        CancellationToken cancellationToken);
    Task<bool> ExpireCleanupAsync(string key, long now, long nextAttemptAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<AudioStoredUpload>> FindRecoverableAsync(long now, int limit, CancellationToken cancellationToken);
    Task<AudioUnbindDisposition> UnbindAsync(string wordId, string? meaningId, AudioLocale locale,
        string expectedAssetId, CancellationToken cancellationToken);
}
