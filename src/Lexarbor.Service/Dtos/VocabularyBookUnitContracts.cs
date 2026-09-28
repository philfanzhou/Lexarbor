using System.Text.Json.Serialization;

namespace Lexarbor.Service.Dtos;

/// <summary>
/// A vocabulary book unit as the administration API returns it. The unit's
/// stable <c>id</c> is the value import and query tasks reference; the number
/// can be replaced later without breaking them.
/// </summary>
public class VocabularyBookUnitDto
{
    public string Id { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public int Number { get; set; }
    public string? Title { get; set; }

    /// <summary>
    /// How many meanings are assigned to the unit when the response is built.
    /// A display snapshot, not a promise about later writes.
    /// </summary>
    public int MeaningCount { get; set; }
}

/// <summary>Vocabulary book unit list response.</summary>
public class VocabularyBookUnitListResponse
{
    public List<VocabularyBookUnitDto> Units { get; set; } = new();
}

/// <summary>
/// Body of <c>POST/PUT /admin/vocabulary-books/{bookId}/units[/{unitId}]</c>.
/// A replace, not a merge: both fields must appear, with <c>null</c> as the
/// explicit form of an absent title, and any unknown field is refused.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyBookUnitUpsertRequest
{
    /// <summary>
    /// Nullable so a body that omits the number can be answered with its own
    /// message rather than the generic binding failure; a present value is
    /// validated as a positive integer by the domain service.
    /// </summary>
    public int? Number { get; init; }

    public required string? Title { get; init; }
}
