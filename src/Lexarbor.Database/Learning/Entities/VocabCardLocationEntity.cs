using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;

[Table("vocab_card_location")]
public sealed class VocabCardLocationEntity
{
    [Column("student_id")] public Guid StudentId { get; set; }
    [Column("word_key")] public string WordKey { get; set; } = string.Empty;
    [Column("meaning_key")] public string MeaningKey { get; set; } = string.Empty;
    [Column("book_id")] public string BookId { get; set; } = string.Empty;
    [Column("word_id")] public string WordId { get; set; } = string.Empty;
    [Column("meaning_id")] public string MeaningId { get; set; } = string.Empty;
    [Column("source")] public short? Source { get; set; }
    [Column("source_question_id")] public Guid? SourceQuestionId { get; set; }
    [Column("recorded_at")] public DateTimeOffset RecordedAt { get; set; }
}
