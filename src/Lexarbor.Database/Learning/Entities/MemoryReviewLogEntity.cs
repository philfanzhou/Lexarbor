using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;

[Table("vocab_review_log")]
public class MemoryReviewLogEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("word_key")]
    [Required]
    public string WordKey { get; set; } = string.Empty;

    [Column("meaning_key")]
    [Required]
    public string MeaningKey { get; set; } = string.Empty;

    [Column("rating")]
    public int Rating { get; set; }

    [Column("reviewed_at")]
    public DateTimeOffset ReviewedAt { get; set; }

    [Column("state_before")]
    public int? StateBefore { get; set; }

    [Column("state_after")]
    public int? StateAfter { get; set; }

    [Column("stability_before")]
    public double? StabilityBefore { get; set; }

    [Column("difficulty_before")]
    public double? DifficultyBefore { get; set; }

    [Column("due_before")]
    public DateTimeOffset? DueBefore { get; set; }

    [Column("stability_after")]
    public double StabilityAfter { get; set; }

    [Column("difficulty_after")]
    public double DifficultyAfter { get; set; }

    [Column("due_after")]
    public DateTimeOffset DueAfter { get; set; }

    [Column("elapsed_days")]
    public int ElapsedDays { get; set; }
}
