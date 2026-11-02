using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("meaning_submission_ai_review")]
public sealed class MeaningSubmissionAiReviewEntity
{
    [Key, Column("id")] public string Id { get; set; } = string.Empty;
    [Column("attempt_id")] public string AttemptId { get; set; } = string.Empty;
    [Column("submission_id")] public string SubmissionId { get; set; } = string.Empty;
    [Column("verdict")] public string Verdict { get; set; } = string.Empty;
    [Column("part_of_speech")] public string? PartOfSpeech { get; set; }
    [Column("meaning")] public string? Meaning { get; set; }
    [Column("phonetic_uk")] public string? PhoneticUk { get; set; }
    [Column("phonetic_us")] public string? PhoneticUs { get; set; }
    [Column("comment")] public string? Comment { get; set; }
    [Column("model")] public string Model { get; set; } = string.Empty;
    [Column("reviewed_at_unix_ms")] public long ReviewedAtUnixMs { get; set; }
    [Column("applied")] public bool Applied { get; set; }
}
