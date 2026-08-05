using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("vocabulary")]
public class VocabularyEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("word")]
    public string Word { get; set; } = string.Empty;

    [Column("phonetic_uk")]
    public string? PhoneticUk { get; set; }

    [Column("phonetic_us")]
    public string? PhoneticUs { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
