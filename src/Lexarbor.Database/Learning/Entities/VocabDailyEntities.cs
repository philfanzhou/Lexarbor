using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;
[Table("vocab_plan_day")]
public class VocabPlanDayEntity
{
    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("local_date")]
    public DateOnly LocalDate { get; set; }

    [Column("daily_tasks")]
    public string? DailyTasks { get; set; }

    [Column("new_words_target")]
    public int NewWordsTarget { get; set; }

    [Column("new_words_learned")]
    public int NewWordsLearned { get; set; }

    [Column("due_in_scope")]
    [Required]
    public string DueInScope { get; set; } = string.Empty;

    [Column("reviewed_in_scope")]
    [Required]
    public string ReviewedInScope { get; set; } = string.Empty;

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("new_words_exhausted")]
    public bool NewWordsExhausted { get; set; }

    [Column("version")]
    public int Version { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public VocabPlanEntity Plan { get; set; } = null!;
}
[Table("vocab_issued_question")]
public class VocabIssuedQuestionEntity
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("plan_id")]
    public Guid? PlanId { get; set; }

    [Column("kind")]
    public int Kind { get; set; }

    [Column("word_key")]
    [Required]
    public string WordKey { get; set; } = string.Empty;

    [Column("meaning_key")]
    [Required]
    public string MeaningKey { get; set; } = string.Empty;

    [Column("word_id")]
    [Required]
    public string WordId { get; set; } = string.Empty;

    [Column("meaning_id")]
    [Required]
    public string MeaningId { get; set; } = string.Empty;

    [Column("book_id")]
    [Required]
    public string BookId { get; set; } = string.Empty;

    [Column("unit_id")]
    public string? UnitId { get; set; }

    /// <summary>Direction: 1=English to Chinese, 2=Chinese to English, 3=fill letters, 7=part-of-speech choice.</summary>
    [Column("direction")]
    public int Direction { get; set; }

    [Column("prompt")]
    [Required]
    public string Prompt { get; set; } = string.Empty;

    [Column("anchor_word")]
    public string? AnchorWord { get; set; }

    [Column("part_of_speech")]
    public string? PartOfSpeech { get; set; }

    [Column("relation_type")]
    public string? RelationType { get; set; }

    [Column("family_source_word")]
    public string? FamilySourceWord { get; set; }

    [Column("family_source_relation_type")]
    public string? FamilySourceRelationType { get; set; }

    [Column("family_source_direction")]
    public string? FamilySourceDirection { get; set; }

    [Column("options_snapshot")]
    public string? OptionsSnapshot { get; set; }

    [Column("correct_option_id")]
    public Guid? CorrectOptionId { get; set; }

    [Column("letter_pattern")]
    public string? LetterPattern { get; set; }

    [Column("answer_word")]
    public string? AnswerWord { get; set; }

    [Column("issued_at")]
    public DateTimeOffset IssuedAt { get; set; }

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("answered_at")]
    public DateTimeOffset? AnsweredAt { get; set; }

    public LearnerIdentity Student { get; set; } = null!;

    public VocabPlanEntity? Plan { get; set; }
}
[Table("vocab_attempt")]
public class VocabAttemptEntity
{
    [Column("issued_question_id")]
    public Guid IssuedQuestionId { get; set; }

    [Column("result")]
    public int Result { get; set; }

    [Column("selected_option_id")]
    public Guid? SelectedOptionId { get; set; }

    [Column("filled_letters")]
    public string? FilledLetters { get; set; }

    [Column("elapsed_ms")]
    public int ElapsedMs { get; set; }

    [Column("rating")]
    public int Rating { get; set; }

    [Column("answered_at")]
    public DateTimeOffset AnsweredAt { get; set; }

    [Column("local_date")]
    public DateOnly LocalDate { get; set; }

    public VocabIssuedQuestionEntity Question { get; set; } = null!;
}
