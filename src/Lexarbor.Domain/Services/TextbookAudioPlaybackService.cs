using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed record TextbookAudioPlaybackResult(string TrackId, AudioPlaybackLink Link);

public sealed class TextbookAudioPlaybackService(ITextbookAudioPlaybackReadRepository repository,
    IAudioObjectStore store, AudioPlaybackSettings settings)
{
    public async Task<TextbookAudioPlaybackResult> GetAdminAsync(string bookId, string unitId, string trackId,
        CancellationToken cancellationToken)
    {
        // Complete the read transaction before starting any provider I/O.
        var snapshot = await repository.GetAdminSnapshotAsync(bookId, unitId, trackId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var location = snapshot.Asset.Location;
            if (await store.StatAsync(location, timeout.Token).WaitAsync(timeout.Token) == null)
                throw new AudioPlaybackUnavailableException();
            timeout.Token.ThrowIfCancellationRequested();
            var link = await store.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(settings.LinkValiditySeconds), timeout.Token)
                .WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return new(snapshot.TrackId, link with { ExpiresAt = link.ExpiresAt.ToUniversalTime() });
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // Never retain provider exceptions or sensitive object locations/signatures.
            throw new AudioPlaybackUnavailableException();
        }
    }
}
