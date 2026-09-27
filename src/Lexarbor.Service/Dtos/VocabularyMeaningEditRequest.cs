using System.Text.Json.Serialization;

namespace Lexarbor.Service.Dtos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyMeaningEditRequest
{
    public required string? PartOfSpeech { get; init; }
    public required string? Meaning { get; init; }
    public required string? Example { get; init; }
}
