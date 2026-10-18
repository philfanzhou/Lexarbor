using System.Text.Json.Serialization;
using Lexarbor.Domain.Models;

namespace Lexarbor.Service.Dtos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TextbookAudioTrackCreateRequest
{
    public required string? Title { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public required int DisplayOrder { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TextbookAudioTrackReplaceRequest
{
    public required string? Title { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public required int DisplayOrder { get; init; }
    public required bool IsAvailable { get; init; }
    public required string? ExpectedVersion { get; init; }
}

public sealed record TextbookAudioTrackDto(string Id, string BookId, string UnitId, string Title, int DisplayOrder,
    bool HasFile, double? DurationSeconds, bool IsAvailable, string Version)
{
    public static TextbookAudioTrackDto FromModel(TextbookAudioTrackModel t) => new(t.Id, t.BookId, t.UnitId, t.Title,
        t.DisplayOrder, t.CurrentAssetId != null, t.DurationSeconds, t.IsAvailable, t.Version);
}

public sealed record PublicTextbookAudioTrackDto(string Id, string BookId, string UnitId, string Title,
    int DisplayOrder, double DurationSeconds, string Version)
{
    public static PublicTextbookAudioTrackDto FromModel(TextbookAudioTrackModel t) => new(t.Id, t.BookId, t.UnitId,
        t.Title, t.DisplayOrder, t.DurationSeconds!.Value, t.Version);
}

public sealed record TextbookAudioPlaybackDto(string TrackId, string Url, DateTimeOffset ExpiresAt);
