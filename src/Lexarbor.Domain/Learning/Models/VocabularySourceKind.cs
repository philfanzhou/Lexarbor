namespace Lexarbor.Learning.Domain.Models;

/// <summary>Where a new-word candidate comes from. Order, quotas and locations are decided per source,
/// and a memory card records the source it was learned from (stored as smallint).</summary>
public enum VocabularySourceKind
{
    /// <summary>The plan's books and units.</summary>
    Plan = 1,

    /// <summary>A notebook item the student or a teacher added.</summary>
    Notebook = 2,

    /// <summary>A notebook item added only through a word family (the item's <c>FamilyOnly</c>).</summary>
    NotebookFamily = 3,
}
