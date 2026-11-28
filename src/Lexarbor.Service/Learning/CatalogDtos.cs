using System.Text.Json.Serialization;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

public sealed record CatalogUnit(string Id, int Number, string? Title, int WordCount, int MeaningCount);

public sealed record CatalogEntryPosition(string UnitId, int UnitNumber, string? Section, string? EntryKind);

public sealed record CatalogEntry(
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
    List<CatalogEntryPosition> Positions);

public sealed record CatalogEntryPage(
    List<CatalogEntry> Items,
    int TotalPage,
    int TotalCount,
    int WordCount);

/// <summary>
/// On-demand question request (Lexarbor POST /api/vocabulary/learning-question). Mode is exactly
/// "new" or "review"; MeaningId selects the meaning; UnitId limits choice candidates to that unit;
/// SameEntryKind=true keeps choice candidates of the same entry kind as the target; Types, when present,
/// restricts the draw to those question type names.
/// </summary>
public sealed record CatalogLearningQuestionRequest(
    string WordId,
    string BookId,
    string MeaningId,
    string Mode,
    string? UnitId,
    bool? SameEntryKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    VocabQuestionRelation? Relation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Types = null);

internal sealed record CatalogUnitList(List<CatalogUnit> Units);
