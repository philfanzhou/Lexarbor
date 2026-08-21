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

    /// <summary>
    /// <c>lower(trim(word))</c>, maintained by SQLite. The value a lookup by
    /// normalized word compares against, so that the comparison lands on an
    /// indexed column instead of on an expression the planner cannot use.
    /// </summary>
    [Column("normalized_word")]
    public string NormalizedWord { get; private set; } = string.Empty;

    [Column("phonetic_uk")]
    public string? PhoneticUk { get; set; }

    [Column("phonetic_us")]
    public string? PhoneticUs { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
