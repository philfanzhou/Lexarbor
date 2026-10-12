using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IAudioReadRepository
{
    /// <summary>Defaults and existing overrides, filtered and paged inside one read snapshot.</summary>
    Task<AudioTargetPage> SearchTargetsAsync(AudioTargetQuery query, CancellationToken cancellationToken);

    /// <summary>Current word, optional owned meaning and both accents in one read snapshot.</summary>
    Task<AudioReadSnapshot> GetSnapshotAsync(string wordId, string? meaningId, CancellationToken cancellationToken);
}
