using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IAudioReadRepository
{
    /// <summary>Current word, optional owned meaning and both accents in one read snapshot.</summary>
    Task<AudioReadSnapshot> GetSnapshotAsync(string wordId, string? meaningId, CancellationToken cancellationToken);
}
