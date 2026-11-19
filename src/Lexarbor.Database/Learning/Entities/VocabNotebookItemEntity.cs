using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;

[Table("vocab_notebook_item")]
public class VocabNotebookItemEntity
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("word_key")]
    public string WordKey { get; set; } = string.Empty;

    [Column("meaning_key")]
    public string MeaningKey { get; set; } = string.Empty;

    [Column("word")]
    public string Word { get; set; } = string.Empty;

    [Column("part_of_speech")]
    public string? PartOfSpeech { get; set; }

    [Column("meaning")]
    public string Meaning { get; set; } = string.Empty;

    [Column("status")]
    public short Status { get; set; }

    [Column("book_id")]
    public string? BookId { get; set; }

    [Column("word_id")]
    public string? WordId { get; set; }

    [Column("meaning_id")]
    public string? MeaningId { get; set; }

    [Column("submission_id")]
    public string? SubmissionId { get; set; }

    [Column("review_comment")]
    public string? ReviewComment { get; set; }

    [Column("original_word")]
    public string? OriginalWord { get; set; }

    [Column("original_part_of_speech")]
    public string? OriginalPartOfSpeech { get; set; }

    [Column("original_meaning")]
    public string? OriginalMeaning { get; set; }

    [Column("family_source_word_key")]
    public string? FamilySourceWordKey { get; set; }

    [Column("family_source_word")]
    public string? FamilySourceWord { get; set; }

    [Column("family_source_word_id")]
    public string? FamilySourceWordId { get; set; }

    [Column("family_relation_type")]
    public string? FamilyRelationType { get; set; }

    [Column("family_direction")]
    public string? FamilyDirection { get; set; }

    [Column("family_relation_id")]
    public string? FamilyRelationId { get; set; }

    [Column("family_source_meaning_id")]
    public string? FamilySourceMeaningId { get; set; }

    [Column("family_source_book_id")]
    public string? FamilySourceBookId { get; set; }

    [Column("family_only")]
    public bool FamilyOnly { get; set; }

    [Column("added_by")]
    public short AddedBy { get; set; }

    [Column("added_by_teacher_id")]
    public Guid? AddedByTeacherId { get; set; }

    [Column("added_at")]
    public DateTimeOffset AddedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public List<VocabNotebookFamilySourceEntity> FamilySources { get; set; } = [];

    public LearnerIdentity? Student { get; set; }
}
