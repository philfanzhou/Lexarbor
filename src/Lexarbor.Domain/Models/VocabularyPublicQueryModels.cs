namespace Lexarbor.Domain.Models;

/// <summary>
/// One unit of an enabled book as the anonymous book-browse endpoints report
/// it. The counts follow the unit, per distinct meaning and per distinct
/// word: a meaning that holds two places of the unit — Section A and Section
/// B, or the word and phrase kinds of one place — counts once.
/// </summary>
public sealed record VocabularyPublicUnit(string Id, int Number, string? Title, int WordCount, int MeaningCount);

/// <summary>Every unit of one enabled book, ordered by unit number.</summary>
public sealed record VocabularyPublicUnitList(IReadOnlyList<VocabularyPublicUnit> Units);

/// <summary>
/// One place a meaning holds: the unit, its number, and the section and entry
/// kind of the place, null when the place is unsectioned or unclassified.
/// </summary>
public sealed record VocabularyPublicEntryPosition(
    string UnitId, int UnitNumber, string? Section, string? EntryKind);

/// <summary>
/// One meaning as the anonymous entries endpoint reports it: the shared word
/// row (spelling, phonetics, normalized spelling), the meaning itself, the
/// <c>lower(trim(...))</c> comparison keys, and the meaning's places inside
/// the requested scope.
/// </summary>
public sealed record VocabularyPublicEntry(
    string WordId,
    string Word,
    string NormalizedWord,
    string? PhoneticUk,
    string? PhoneticUs,
    string MeaningId,
    string? PartOfSpeech,
    string Meaning,
    string MeaningKey,
    string? Example,
    IReadOnlyList<VocabularyPublicEntryPosition> Positions);

/// <summary>
/// One page of a book's (or one unit's) meanings. <c>TotalCount</c> counts
/// meanings in the scope; <c>WordCount</c> counts distinct words behind them.
/// </summary>
public sealed record VocabularyPublicEntryPage(
    IReadOnlyList<VocabularyPublicEntry> Items, int TotalCount, int TotalPage, int WordCount);
