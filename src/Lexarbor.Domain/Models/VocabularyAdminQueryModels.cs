namespace Lexarbor.Domain.Models;

public sealed record VocabularyAdminBook(string Id, string BookName, bool Status);

/// <summary>A unit a meaning is assigned to, identified for administrative reads.</summary>
public sealed record VocabularyAdminUnit(string UnitId, int Number, string? Title);

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
/// One unit of one book, paged over the words whose meanings are assigned to
/// it. The counts follow the unit, not a keyword: a keyword narrows the page,
/// never the totals.
/// </summary>
public sealed record VocabularyAdminUnitContent(
    VocabularyBookModel Book,
    VocabularyBookUnitModel Unit,
    int WordCount,
    int MeaningCount,
    VocabularyAdminPage Page);
