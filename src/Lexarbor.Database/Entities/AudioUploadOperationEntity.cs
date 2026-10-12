using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

[Table("audio_upload_operation")]
public sealed class AudioUploadOperationEntity
{
    [Key]
    [Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Column("request_fingerprint")]
    public string RequestFingerprint { get; set; } = string.Empty;

    [Column("word_id")]
    public string WordId { get; set; } = string.Empty;

    [Column("meaning_id")]
    public string? MeaningId { get; set; }

    [Column("locale")]
    public AudioLocale Locale { get; set; }

    [Column("source_revision")]
    public string SourceRevision { get; set; } = string.Empty;

    [Column("expected_asset_id")]
    public string? ExpectedAssetId { get; set; }

    [Column("asset_id")]
    public string AssetId { get; set; } = string.Empty;

    [Column("producer")]
    public string? Producer { get; set; }

    [Column("voice")]
    public string? Voice { get; set; }

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

    [Column("receipt_source_revision")]
    public string? ReceiptSourceRevision { get; set; }

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
