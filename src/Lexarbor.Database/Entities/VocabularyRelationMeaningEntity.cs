using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("vocabulary_relation_meaning")]
public sealed class VocabularyRelationMeaningEntity
{
    [Column("relation_id")] public string RelationId { get; set; } = string.Empty;
    [Column("source_meaning_id")] public string SourceMeaningId { get; set; } = string.Empty;
    [Column("target_meaning_id")] public string TargetMeaningId { get; set; } = string.Empty;
}
