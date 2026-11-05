using System.Text.Json.Serialization;
using Lexarbor.Domain.Models;

namespace Lexarbor.Service.Dtos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyRelationRequest
{
    public required string SourceWordId { get; init; }
    public required string TargetWordId { get; init; }
    public required string Type { get; init; }
    public string? UsageNote { get; init; }
    public bool Confirm { get; init; }
    public VocabularyRelationInput ToInput() => new(SourceWordId, TargetWordId, Type, UsageNote, Confirm);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyRelationReplaceRequest
{
    public required string SourceWordId { get; init; }
    public required string TargetWordId { get; init; }
    public required string Type { get; init; }
    public required string? UsageNote { get; init; }
    public required bool Confirm { get; init; }
    public required long ExpectedVersion { get; init; }
    public VocabularyRelationInput ToInput() => new(SourceWordId, TargetWordId, Type, UsageNote, Confirm);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VocabularyRelationBatchRequest
{
    public required List<VocabularyRelationRequest?> Items { get; init; }
}
