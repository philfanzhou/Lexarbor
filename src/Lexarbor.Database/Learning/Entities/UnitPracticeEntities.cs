using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Learning.Database.Entities;

[Table("vocab_unit_practice")]
[PrimaryKey(nameof(StudentId))]
public sealed class UnitPracticeSession
{
    [Column("student_id")] public Guid StudentId { get; set; }
    [Column("session_id")] public Guid SessionId { get; set; }
    [Column("book_id")] public required string BookId { get; set; }
    [Column("unit_id")] public required string UnitId { get; set; }
    [Column("round")] public int Round { get; set; } = 1;
}

[Table("vocab_unit_practice_item")]
[PrimaryKey(nameof(SessionId), nameof(Position))]
public sealed class UnitPracticeItem
{
    [Column("session_id")] public Guid SessionId { get; set; }
    [Column("position")] public int Position { get; set; }
    [Column("word_id")] public required string WordId { get; set; }
    [Column("meaning_id")] public required string MeaningId { get; set; }
    [Column("word")] public required string Word { get; set; }
    [Column("meaning")] public required string Meaning { get; set; }
    [Column("part_of_speech")] public string? PartOfSpeech { get; set; }
    [Column("spelling_hint")] public string? SpellingHint { get; set; }
    [Column("first_question_id")] public Guid FirstQuestionId { get; set; }
    [Column("first_correct")] public bool? FirstCorrect { get; set; }
    [Column("retry_question_id")] public Guid? RetryQuestionId { get; set; }
    [Column("retry_round")] public int RetryRound { get; set; }
    [Column("retry_correct")] public bool? RetryCorrect { get; set; }
}
