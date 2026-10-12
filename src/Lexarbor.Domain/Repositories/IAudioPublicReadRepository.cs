using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IAudioPublicReadRepository
{
    /// <summary>Enabled book, word/meaning membership and audio inputs/bindings in one completed snapshot.</summary>
    Task<AudioReadSnapshot> GetPublicSnapshotAsync(string bookId, string wordId, string? meaningId,
        CancellationToken cancellationToken);
}
