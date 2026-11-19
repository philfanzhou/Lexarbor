using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;

[Table("vocab_memory_card")]
public class MemoryCardEntity
{
    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("word_key")]
    [Required]
    public string WordKey { get; set; } = string.Empty;

    [Column("meaning_key")]
    [Required]
    public string MeaningKey { get; set; } = string.Empty;

    [Column("state")]
    public int State { get; set; }

    [Column("stability")]
    public double? Stability { get; set; }

    [Column("difficulty")]
    public double? Difficulty { get; set; }

    [Column("due_at")]
    public DateTimeOffset DueAt { get; set; }

    [Column("reps")]
    public int Reps { get; set; }

    [Column("lapses")]
    public int Lapses { get; set; }

    [Column("last_reviewed_at")]
    public DateTimeOffset LastReviewedAt { get; set; }

    [Column("first_learned_at")]
    public DateTimeOffset FirstLearnedAt { get; set; }

    [Column("first_learned_plan_id")]
    public Guid? FirstLearnedPlanId { get; set; }

    [Column("learning_step")]
    public int? LearningStep { get; set; }

    [Column("version")]
    public int Version { get; set; }

    public LearnerIdentity Student { get; set; } = null!;
}
