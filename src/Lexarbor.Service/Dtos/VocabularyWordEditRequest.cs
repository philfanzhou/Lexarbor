using System.Text.Json.Serialization;

namespace Lexarbor.Service.Dtos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyWordEditRequest
{
    public required string? Word { get; init; }
    public required string? PhoneticUk { get; init; }
    public required string? PhoneticUs { get; init; }
}
