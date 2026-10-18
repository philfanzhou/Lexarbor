using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

[Table("textbook_audio_upload_operation")]
public sealed class TextbookAudioUploadOperationEntity
{
    [Key]
    [Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Column("request_fingerprint")]
    public string RequestFingerprint { get; set; } = string.Empty;

    [Column("book_id")] public string BookId { get; set; } = string.Empty;
    [Column("unit_id")] public string UnitId { get; set; } = string.Empty;
    [Column("track_id")] public string TrackId { get; set; } = string.Empty;
    [Column("expected_version")] public string ExpectedVersion { get; set; } = string.Empty;
    [Column("duration_seconds")] public double DurationSeconds { get; set; }

    [Column("asset_id")]
    public string AssetId { get; set; } = string.Empty;

    [Column("state")]
    public AudioUploadState State { get; set; }

    [Column("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; set; }

    [Column("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; set; }

    [Column("upload_lease_owner")]
    public string? UploadLeaseOwner { get; set; }

    [Column("upload_lease_fence")]
    public long UploadLeaseFence { get; set; }

    [Column("upload_lease_expires_at_unix_ms")]
    public long? UploadLeaseExpiresAtUnixMs { get; set; }

    [Column("receipt_status_code")]
    public int? ReceiptStatusCode { get; set; }

    [Column("receipt_asset_id")]
    public string? ReceiptAssetId { get; set; }

    [Column("receipt_track_id")] public string? ReceiptTrackId { get; set; }
    [Column("receipt_version")] public string? ReceiptVersion { get; set; }
    [Column("receipt_duration_seconds")] public double? ReceiptDurationSeconds { get; set; }

    [Column("receipt_error_code")]
    public string? ReceiptErrorCode { get; set; }

    [Column("completed_at_unix_ms")]
    public long? CompletedAtUnixMs { get; set; }

    [Column("cleanup_lease_owner")]
    public string? CleanupLeaseOwner { get; set; }

    [Column("cleanup_lease_fence")]
    public long CleanupLeaseFence { get; set; }

    [Column("cleanup_lease_expires_at_unix_ms")]
    public long? CleanupLeaseExpiresAtUnixMs { get; set; }

    [Column("cleanup_return_state")]
    public AudioUploadState? CleanupReturnState { get; set; }

    [Column("cleanup_attempts")]
    public int CleanupAttempts { get; set; }

    [Column("cleanup_next_attempt_at_unix_ms")]
    public long? CleanupNextAttemptAtUnixMs { get; set; }

    [Column("cleanup_last_error_code")]
    public string? CleanupLastErrorCode { get; set; }

    [Column("cleanup_observed_at_unix_ms")]
    public long? CleanupObservedAtUnixMs { get; set; }

}
