namespace Lexarbor.Domain.Models;

// Internal persistence records. Object locations never belong in HTTP receipts.
public sealed record TextbookAudioUploadReceipt(int StatusCode, string? TrackId, string? Version,
    double? DurationSeconds, string? AssetId, string? ErrorCode, long CompletedAtUnixMs);
public sealed record TextbookAudioUploadOperation(string IdempotencyKey, string RequestFingerprint,
    string BookId, string UnitId, string TrackId, string ExpectedVersion, double DurationSeconds, string AssetId,
    AudioUploadState State, long CreatedAtUnixMs, long UpdatedAtUnixMs, AudioLease UploadLease,
    TextbookAudioUploadReceipt? Receipt, AudioCleanupProgress Cleanup);
