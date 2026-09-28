using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

/// <summary>
/// The assignment of one meaning to one place of one unit. A meaning can be
/// assigned to several units of its book, and — because books split their
/// units into Section A and Section B and classify entries as words or
/// phrases — to several places of the same unit; the quadruple
/// <c>(unit_id, meaning_id, section, entry_kind)</c> is the primary key, so
/// repeating an assignment is a conflict rather than a second row.
/// </summary>
/// <remarks>
/// <para>
/// <c>section</c> and <c>entry_kind</c> are stored as non-nullable sentinels
/// rather than NULL: SQLite treats NULLs in a composite primary key as mutually
/// unequal, so NULL storage would let the same unsectioned or unclassified
/// assignment be inserted twice and cannot back the idempotent re-import the
/// batch contract promises. The sentinels are the empty string, with
/// <c>A</c> and <c>B</c> the only section names and <c>word</c> and
/// <c>phrase</c> the only entry kinds; a CHECK constraint enforces each
/// domain. The domain model hides the sentinels behind nullable
/// <see cref="string"/>s where null means no section or no kind; the
/// repository translates.
/// </para>
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

    [Column("section")]
    public string Section { get; set; } = string.Empty;

    [Column("entry_kind")]
    public string EntryKind { get; set; } = string.Empty;

    public virtual VocabularyBookUnitEntity? Unit { get; set; }

    public virtual VocabularyMeaningEntity? Meaning { get; set; }
}
