namespace Lexarbor.Domain.Models;

public sealed record TextbookAudioTrackModel(
    string Id, string BookId, string UnitId, string Title, int DisplayOrder,
    string? CurrentAssetId, double? DurationSeconds, bool IsAvailable, string Version);

public sealed record TextbookAudioTrackPage(IReadOnlyList<TextbookAudioTrackModel> Items, int TotalCount, int TotalPage);
