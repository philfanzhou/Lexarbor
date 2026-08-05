using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("vocabulary_meaning")]
public class VocabularyMeaningEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("vocabulary_id")]
    public string VocabularyId { get; set; } = string.Empty;

    [Column("book_id")]
    public string BookId { get; set; } = string.Empty;

    [Column("part_of_speech")]
    public string? PartOfSpeech { get; set; }

    [Column("meaning")]
    public string Meaning { get; set; } = string.Empty;

    [Column("normalized_part_of_speech")]
    public string NormalizedPartOfSpeech { get; private set; } = string.Empty;

    [Column("normalized_meaning")]
    public string NormalizedMeaning { get; private set; } = string.Empty;

    [Column("example")]
    public string? Example { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [ForeignKey("VocabularyId")]
    public virtual VocabularyEntity? Vocabulary { get; set; }

    [ForeignKey("BookId")]
    public virtual VocabularyBookEntity? Book { get; set; }
}
