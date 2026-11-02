using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("meaning_submission_review_check")]
public sealed class MeaningSubmissionReviewCheckEntity
{
    [Key, Column("record_id")] public string RecordId { get; set; } = string.Empty;
    [Column("checked_at_unix_ms")] public long CheckedAtUnixMs { get; set; }
}
