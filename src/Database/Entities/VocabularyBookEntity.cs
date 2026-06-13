using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Vocabulary.Database.Entities;

[Table("vocabulary_book")]
public class VocabularyBookEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("book_name")]
    public string BookName { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("publisher")]
    public string? Publisher { get; set; }

    [Column("education_level")]
    public string? EducationLevel { get; set; }

    [Column("grade")]
    public string? Grade { get; set; }

    [Column("category")]
    public string? Category { get; set; }

    [Column("display_order")]
    public int DisplayOrder { get; set; }

    [Column("status")]
    public bool Status { get; set; }

    [Column("icon_url")]
    public string? IconUrl { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}