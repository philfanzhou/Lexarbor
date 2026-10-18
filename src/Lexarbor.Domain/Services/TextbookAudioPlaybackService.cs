using Lexarbor.Domain.Exceptions;
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
        return await SignAsync(snapshot, cancellationToken);
    }

    public Task<TextbookAudioTrackPage> ListPublicAsync(string bookId, string unitId, int page, int size,
        CancellationToken cancellationToken)
    {
        bookId = NormalizeId(bookId); unitId = NormalizeId(unitId);
        if (page < 0 || size < 0) throw new DomainValidationException("Page and size must be nonnegative integers.");
        return repository.GetPublicPageAsync(bookId, unitId, page == 0 ? 1 : page,
            size == 0 ? 20 : Math.Min(size, 100), cancellationToken);
    }

    public async Task<TextbookAudioPlaybackResult> GetPublicAsync(string bookId, string unitId, string trackId,
        CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetPublicSnapshotAsync(NormalizeId(bookId), NormalizeId(unitId), NormalizeId(trackId), cancellationToken);
        return await SignAsync(snapshot, cancellationToken);
    }

    private static string NormalizeId(string? id) => !string.IsNullOrWhiteSpace(id)
        ? id.Trim() : throw new DomainValidationException("An ID is required.");

    private async Task<TextbookAudioPlaybackResult> SignAsync(TextbookAudioPlaybackSnapshot snapshot, CancellationToken cancellationToken)
    {
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
