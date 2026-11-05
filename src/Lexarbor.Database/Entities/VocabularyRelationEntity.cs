using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("vocabulary_relation")]
public sealed class VocabularyRelationEntity
{
    [Key, Column("id")] public string Id { get; set; } = string.Empty;
    [Column("source_word_id")] public string SourceWordId { get; set; } = string.Empty;
    [Column("target_word_id")] public string TargetWordId { get; set; } = string.Empty;
    [Column("type")] public string Type { get; set; } = string.Empty;
    [Column("usage_note")] public string? UsageNote { get; set; }
    [Column("status")] public string Status { get; set; } = "needs_review";
    [Column("review_reason")] public string? ReviewReason { get; set; } = "new_relation";
    [Column("origin_kind")] public string OriginKind { get; set; } = string.Empty;
    [Column("origin_reference")] public string? OriginReference { get; set; }
    [Column("version")] public long Version { get; set; } = 1;
    [Column("created_at_unix_ms")] public long CreatedAtUnixMs { get; set; }
    [Column("updated_at_unix_ms")] public long UpdatedAtUnixMs { get; set; }
    [Column("last_confirmed_at_unix_ms")] public long? LastConfirmedAtUnixMs { get; set; }
}
