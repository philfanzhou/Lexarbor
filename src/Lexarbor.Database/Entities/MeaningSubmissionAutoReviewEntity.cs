using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("meaning_submission_auto_review")]
public sealed class MeaningSubmissionAutoReviewEntity
{
    [Key, Column("submission_id"), MaxLength(100)] public string SubmissionId { get; set; } = "";
    [Column("attempt_id"), MaxLength(100)] public string AttemptId { get; set; } = "";
    [Column("owner"), MaxLength(100)] public string? Owner { get; set; }
    [Column("epoch")] public long Epoch { get; set; }
    [Column("lease_until_unix_ms")] public long LeaseUntilUnixMs { get; set; }
    [Column("next_attempt_at_unix_ms")] public long NextAttemptAtUnixMs { get; set; }
    [Column("consecutive_failures")] public int ConsecutiveFailures { get; set; }
    [Column("last_failure_code"), MaxLength(40)] public string? LastFailureCode { get; set; }
    [Column("last_failure_at_unix_ms")] public long? LastFailureAtUnixMs { get; set; }
    [Column("validated_review_json")] public string? ValidatedReviewJson { get; set; }
}
