namespace Lexarbor.Domain.Models;

public sealed record VocabularyAdminBook(string Id, string BookName, bool Status);

/// <summary>
/// A unit a meaning is assigned to, identified for administrative reads. A
/// meaning that holds two places of one unit reads as two entries whose
/// <c>Section</c> differs; null is the unsectioned place.
/// </summary>
public sealed record VocabularyAdminUnit(string UnitId, int Number, string? Title, string? Section);

/// <summary>One meaning of an administrative detail result together with its unit assignments.</summary>
public sealed record VocabularyAdminMeaningDetail(
    VocabularyMeaningModel Meaning,
    IReadOnlyList<VocabularyAdminUnit> Units);

public sealed record VocabularyAdminWord(VocabularyModel Word, IReadOnlyList<VocabularyAdminBook> Books,
    IReadOnlyList<VocabularyAdminMeaningDetail> Meanings);
public sealed record VocabularyAdminPage(IReadOnlyList<VocabularyAdminWord> Items, int TotalCount, int TotalPage);
public sealed record VocabularyAdminContent(VocabularyBookModel Book, int WordCount, int MeaningCount,
    VocabularyAdminPage Page);

/// <summary>
/// How many assignment places a unit holds per section, counted over the whole
/// unit whatever the query narrows the page to: Section A, Section B, and the
/// unsectioned places.
/// </summary>
public sealed record VocabularyAdminSectionCounts(int SectionA, int SectionB, int NoSection);

/// <summary>
/// One unit of one book, paged over the words whose meanings are assigned to
/// it. The counts follow the unit, not a keyword: a keyword narrows the page,
/// never the totals. A <c>section</c> narrows the page and the counts to one
/// section's places of the unit — meanings are still counted per distinct
/// meaning, so one meaning in both sections counts once — while the section
/// counts always speak for the whole unit.
/// </summary>
public sealed record VocabularyAdminUnitContent(
    VocabularyBookModel Book,
    VocabularyBookUnitModel Unit,
    int WordCount,
    int MeaningCount,
    VocabularyAdminSectionCounts SectionCounts,
    VocabularyAdminPage Page);
