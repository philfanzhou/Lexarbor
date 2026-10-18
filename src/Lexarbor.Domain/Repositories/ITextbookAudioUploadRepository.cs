using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public sealed record TextbookAudioUploadRequest(string IdempotencyKey, string RequestFingerprint,
    string BookId, string UnitId, string TrackId, string ExpectedVersion, double DurationSeconds,
    string Sha256, long ByteLength, string Bucket, string KeyPrefix = "audio/");
public sealed record TextbookAudioStoredUpload(TextbookAudioUploadOperation Operation, AudioAsset Asset);
public sealed record TextbookAudioReservation(AudioReservationDisposition Disposition, TextbookAudioStoredUpload? Upload);

/// <summary>
/// Short admitted transactions only; an enclosing UnitOfWork is rejected. Media validation and
/// storage IO belong to the caller, outside these calls. Times are UTC Unix milliseconds.
/// Owners and fences must match, expiry is exclusive, and receipts survive target deletion/cleanup.
/// The fingerprint is SHA256 of the UTF-8 JSON array
/// ["textbook-audio-upload",1,bookId,unitId,trackId,expectedVersion,sha256,byteLength].
/// </summary>
public interface ITextbookAudioUploadRepository
{
    Task<TextbookAudioStoredUpload?> GetAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task<TextbookAudioReservation> ReserveAsync(TextbookAudioUploadRequest request, string owner, long now, long leaseExpiresAt, CancellationToken cancellationToken);
    Task<bool> BeginTransferAsync(string key, AudioLease lease, long now, CancellationToken cancellationToken);
    Task<bool> RenewAsync(string key, AudioLease lease, long now, long leaseExpiresAt, CancellationToken cancellationToken);
    Task<TextbookAudioUploadOperation?> CompleteAsync(string key, AudioLease lease, long now, CancellationToken cancellationToken);
    Task<bool> AbandonAsync(string key, AudioLease lease, long now, int statusCode, string errorCode, CancellationToken cancellationToken);
    Task<bool> ExpireAsync(string key, long now, CancellationToken cancellationToken);
    Task<IReadOnlyList<TextbookAudioStoredUpload>> FindRecoverableAsync(long now, int limit, CancellationToken cancellationToken);
    Task<TextbookAudioStoredUpload?> ClaimCleanupAsync(string key, string owner, long now, long leaseExpiresAt, CancellationToken cancellationToken);
    Task<bool> ReleaseCleanupAsync(string key, AudioLease lease, long now, long nextAttemptAt, string errorCode, CancellationToken cancellationToken);
    Task<bool> ObserveCleanupAsync(string key, AudioLease lease, long now, long nextAttemptAt, CancellationToken cancellationToken);
    Task<bool> ExpireCleanupAsync(string key, long now, long nextAttemptAt, CancellationToken cancellationToken);
}
