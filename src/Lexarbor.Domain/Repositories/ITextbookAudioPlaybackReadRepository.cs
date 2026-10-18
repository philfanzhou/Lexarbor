using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public sealed record TextbookAudioPlaybackSnapshot(string TrackId, AudioAsset Asset);

public interface ITextbookAudioPlaybackReadRepository
{
    /// <summary>Validates ownership and captures the published current file inside one completed read snapshot.</summary>
    Task<TextbookAudioPlaybackSnapshot> GetAdminSnapshotAsync(string bookId, string unitId, string trackId,
        CancellationToken cancellationToken);
}
