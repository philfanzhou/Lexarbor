using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

/// <summary>
/// The assignment of one meaning to one unit. A meaning can be assigned to
/// several units of its book, and a unit holds several meanings; the pair is
/// the primary key, so repeating an assignment is a conflict rather than a
/// second row.
/// </summary>
/// <remarks>
/// <para>
/// <c>book_id</c> is deliberately redundant: it duplicates the book both the
/// unit and the meaning already carry, and the two composite foreign keys
/// below compare against it, so a row can only exist when the unit's book and
/// the meaning's book are the same book. Cross-book assignments are therefore
/// impossible to store, not merely rejected by the application layer.
/// </para>
/// <para>
/// Both foreign keys cascade: deleting a unit or a meaning removes its
/// assignments without touching the surviving side, and deleting a book
/// removes the units (whose assignments follow) once the meaning-level
/// <c>RESTRICT</c> has been satisfied first.
/// </para>
/// </remarks>
[Table("vocabulary_meaning_unit")]
public class VocabularyMeaningUnitEntity
{
    [Column("unit_id")]
    public string UnitId { get; set; } = string.Empty;

    [Column("meaning_id")]
    public string MeaningId { get; set; } = string.Empty;

    [Column("book_id")]
    public string BookId { get; set; } = string.Empty;

    public virtual VocabularyBookUnitEntity? Unit { get; set; }

    public virtual VocabularyMeaningEntity? Meaning { get; set; }
}
