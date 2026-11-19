using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;

[Table("vocab_notebook_family_source")]
public sealed class VocabNotebookFamilySourceEntity
{
    [Column("id")] public Guid Id { get; set; }
    [Column("notebook_item_id")] public Guid NotebookItemId { get; set; }
    [Column("source_word_key")] public string SourceWordKey { get; set; } = string.Empty;
    [Column("source_word")] public string SourceWord { get; set; } = string.Empty;
    [Column("source_word_id")] public string SourceWordId { get; set; } = string.Empty;
    [Column("relation_type")] public string RelationType { get; set; } = string.Empty;
    [Column("direction")] public string Direction { get; set; } = string.Empty;
    [Column("relation_id")] public string? RelationId { get; set; }
    [Column("source_meaning_id")] public string SourceMeaningId { get; set; } = string.Empty;
    [Column("source_book_id")] public string SourceBookId { get; set; } = string.Empty;
    [Column("target_word_id")] public string TargetWordId { get; set; } = string.Empty;
    [Column("target_meaning_id")] public string TargetMeaningId { get; set; } = string.Empty;
    [Column("target_book_id")] public string TargetBookId { get; set; } = string.Empty;
    public VocabNotebookItemEntity? NotebookItem { get; set; }
}
