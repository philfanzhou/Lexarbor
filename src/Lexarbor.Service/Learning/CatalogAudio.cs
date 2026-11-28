using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

/// <summary>Strict upstream projection; unknown metadata is intentionally discarded.</summary>
public sealed record CatalogAudio(
    [property: JsonRequired] string Locale,
    [property: JsonRequired] string Status,
    [property: JsonRequired] string? Url,
    [property: JsonRequired, JsonConverter(typeof(AudioExpiryConverter))] DateTimeOffset? ExpiresAt);

internal sealed record CatalogAudioList(List<CatalogAudio?>? Items);
