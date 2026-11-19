using System.Text.Json.Serialization;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

public sealed record LexarborBook(
    string Id,
    string BookName,
    string? Description,
    string? Category,
    string? EducationLevel,
    string? Grade,
    string? Publisher,
    int? DisplayOrder,
    bool? Status,
    string? IconUrl);

public sealed record LexarborUnit(string Id, int Number, string? Title, int WordCount, int MeaningCount);

public sealed record LexarborEntryPosition(string UnitId, int UnitNumber, string? Section, string? EntryKind);

public sealed record LexarborEntry(
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
    List<LexarborEntryPosition> Positions);

public sealed record LexarborEntryPage(
    List<LexarborEntry> Items,
    int TotalPage,
    int TotalCount,
    int WordCount);

/// <summary>
/// On-demand question request (Lexarbor POST /api/vocabulary/learning-question). Mode is exactly
/// "new" or "review"; MeaningId selects the meaning; UnitId limits choice candidates to that unit;
/// SameEntryKind=true keeps choice candidates of the same entry kind as the target.
/// </summary>
public sealed record LexarborLearningQuestionRequest(
    string WordId,
    string BookId,
    string MeaningId,
    string Mode,
    string? UnitId,
    bool? SameEntryKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    VocabQuestionRelation? Relation = null);

internal sealed record LexarborBookList(List<LexarborBook> Books);

internal sealed record LexarborUnitList(List<LexarborUnit> Units);
