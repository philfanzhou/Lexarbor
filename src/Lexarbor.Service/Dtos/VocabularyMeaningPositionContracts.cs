using System.Text.Json.Serialization;

namespace Lexarbor.Service.Dtos;

/// <summary>One exact unit/section/entry-kind position in a replacement request.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyMeaningPositionRequest
{
    public required string UnitId { get; init; }
    public required string? Section { get; init; }
    public required string? EntryKind { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyMeaningPositionMoveRequest
{
    public required VocabularyMeaningPositionRequest From { get; init; }
    public required VocabularyMeaningPositionRequest To { get; init; }
}
