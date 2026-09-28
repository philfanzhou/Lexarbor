namespace Lexarbor.Domain.Models;

public class VocabularyMeaningUnitModel
{
    public string UnitId { get; set; } = string.Empty;
    public string MeaningId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;

    /// <summary>
    /// The section of the unit the assignment sits in: <c>A</c>, <c>B</c>, or
    /// null for an assignment the book does not split into sections. A meaning
    /// may hold several positions of the same unit, one per section.
    /// </summary>
    public string? Section { get; set; }

    /// <summary>
    /// Whether the entry at this position is a <c>word</c> or a <c>phrase</c>,
    /// or null when the administrator did not classify it. A meaning may hold
    /// several positions of the same place of a unit, one per kind; the kind is
    /// a property of the assignment, never inferred from the entry's text.
    /// </summary>
    public string? EntryKind { get; set; }
}

/// <summary>
/// The section rule every writer of meaning-to-unit assignments shares: a
/// section is absent, or exactly <c>A</c> or <c>B</c> after trimming. Case is
/// significant — <c>a</c> is invalid, matching the unit-number rule that an
/// assignment must name its place exactly rather than be guessed.
/// </summary>
public static class VocabularyMeaningUnitSections
{
    /// <summary>Trims the stored form: null for no section, the trimmed value otherwise.</summary>
    public static string? NormalizeOrNull(string? section)
    {
        return string.IsNullOrWhiteSpace(section) ? null : section.Trim();
    }

    /// <summary>Whether a normalized section value names a section that exists.</summary>
    public static bool IsValid(string? normalizedSection)
    {
        return normalizedSection is null or "A" or "B";
    }
}

/// <summary>
/// The entry-kind rule every writer of meaning-to-unit assignments shares: a
/// kind is absent, or exactly <c>word</c> or <c>phrase</c> after trimming. Case
/// is significant — <c>Word</c> is invalid, matching the section rule: the
/// administrator classifies the entry explicitly, and a guessed kind would put
/// a meaning in a place it was never assigned to.
/// </summary>
public static class VocabularyMeaningUnitEntryKinds
{
    /// <summary>Trims the stored form: null for no kind, the trimmed value otherwise.</summary>
    public static string? NormalizeOrNull(string? entryKind)
    {
        return string.IsNullOrWhiteSpace(entryKind) ? null : entryKind.Trim();
    }

    /// <summary>Whether a normalized kind value names a kind that exists.</summary>
    public static bool IsValid(string? normalizedEntryKind)
    {
        return normalizedEntryKind is null or "word" or "phrase";
    }
}
