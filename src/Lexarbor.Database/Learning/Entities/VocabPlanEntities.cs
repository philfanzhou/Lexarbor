using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;

[Table("vocab_plan")]
public class VocabPlanEntity
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("segments")]
    [Required]
    public string Segments { get; set; } = "[]";

    [Column("daily_new_words")]
    public int DailyNewWords { get; set; }

    [Column("controller")]
    public int Controller { get; set; }

    [Column("controller_teacher_id")]
    public Guid? ControllerTeacherId { get; set; }

    [Column("status")]
    public int Status { get; set; }

    [Column("start_date")]
    public DateOnly StartDate { get; set; }

    [Column("total_words")]
    public int TotalWords { get; set; }

    [Column("version")]
    public int Version { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public LearnerIdentity Student { get; set; } = null!;
}
[Table("vocab_plan_change")]
public class VocabPlanChangeEntity
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("actor_type")]
    public int ActorType { get; set; }

    [Column("actor_teacher_id")]
    public Guid? ActorTeacherId { get; set; }

    [Column("action")]
    [Required]
    public string Action { get; set; } = string.Empty;

    [Column("before_values")]
    public string? BeforeValues { get; set; }

    [Column("after_values")]
    public string? AfterValues { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    public VocabPlanEntity Plan { get; set; } = null!;
}
