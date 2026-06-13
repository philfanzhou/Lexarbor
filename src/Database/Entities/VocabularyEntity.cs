using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Vocabulary.Database.Entities;

[Table("vocabulary")]
public class VocabularyEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("word")]
    public string Word { get; set; } = string.Empty;

    [Column("phonetic")]
    public string? Phonetic { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
