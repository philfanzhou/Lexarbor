using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class AudioPublicPlaybackService(IAudioPublicReadRepository repository, AudioPlaybackService playback)
{
    public async Task<IReadOnlyList<AudioPlaybackResult>> GetAsync(string bookId, string wordId, string? meaningId,
        AudioLocale? locale, CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetPublicSnapshotAsync(bookId, wordId, meaningId, cancellationToken);
        var results = new List<AudioPlaybackResult>();
        // Both accents share the authorization snapshot; network I/O starts after it closes.
        foreach (var accent in locale.HasValue ? [locale.Value] : Enum.GetValues<AudioLocale>())
            results.Add(await playback.GetAsync(snapshot, accent, cancellationToken));
        return results;
    }
}
