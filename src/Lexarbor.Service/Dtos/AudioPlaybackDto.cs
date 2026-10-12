namespace Lexarbor.Service.Dtos;

public sealed record AudioPlaybackDto(string Locale, string Status, string? Source, string? AssetId,
    string? SourceRevision, string? Url, DateTimeOffset? ExpiresAt);
