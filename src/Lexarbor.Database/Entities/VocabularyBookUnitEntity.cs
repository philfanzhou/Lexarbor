using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

/// <summary>
/// A unit of a vocabulary book. Unit numbers are unique per book and carry no
/// meaning beyond ordering; titles are display text only.
/// </summary>
[Table("vocabulary_book_unit")]
public class VocabularyBookUnitEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("book_id")]
    public string BookId { get; set; } = string.Empty;

    [Column("number")]
    public int Number { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [ForeignKey("BookId")]
    public virtual VocabularyBookEntity? Book { get; set; }
}
