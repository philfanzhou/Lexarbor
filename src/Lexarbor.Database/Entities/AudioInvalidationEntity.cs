using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

[Table("audio_invalidation")]
public sealed class AudioInvalidationEntity
{
    [Key, Column("asset_id")] public string AssetId { get; set; } = string.Empty;
    [Column("bucket")] public string Bucket { get; set; } = string.Empty;
    [Column("object_key")] public string ObjectKey { get; set; } = string.Empty;
    [Column("word_id")] public string WordId { get; set; } = string.Empty;
    [Column("meaning_id")] public string MeaningId { get; set; } = string.Empty;
    [Column("locale")] public AudioLocale Locale { get; set; }
    [Column("authorization_epoch")] public long AuthorizationEpoch { get; set; }
    [Column("reason")] public string Reason { get; set; } = "specific_phonetic_changed";
    [Column("state")] public AudioInvalidationState State { get; set; }
    [Column("created_at_unix_ms")] public long CreatedAtUnixMs { get; set; }
    [Column("updated_at_unix_ms")] public long UpdatedAtUnixMs { get; set; }
    [Column("lease_owner")] public string? LeaseOwner { get; set; }
    [Column("lease_fence")] public long LeaseFence { get; set; }
    [Column("lease_expires_at_unix_ms")] public long? LeaseExpiresAtUnixMs { get; set; }
    [Column("attempts")] public int Attempts { get; set; }
    [Column("next_attempt_at_unix_ms")] public long NextAttemptAtUnixMs { get; set; }
    [Column("delete_started_at_unix_ms")] public long? DeleteStartedAtUnixMs { get; set; }
    [Column("observed_at_unix_ms")] public long? ObservedAtUnixMs { get; set; }
    [Column("error_code")] public string? ErrorCode { get; set; }
}
