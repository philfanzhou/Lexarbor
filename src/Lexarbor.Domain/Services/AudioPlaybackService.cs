using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed record AudioPlaybackSettings(int LinkValiditySeconds, int TimeoutSeconds);
public sealed record AudioPlaybackResult(AudioSelection Selection, AudioPlaybackLink? Link);
public sealed class AudioPlaybackUnavailableException() : Exception("Audio storage is unavailable.");

/// <summary>Selects inside one completed read snapshot, then accesses storage without a database transaction.</summary>
public sealed class AudioPlaybackService(IAudioReadRepository repository, IAudioObjectStore store, AudioPlaybackSettings settings)
{
    public async Task<AudioPlaybackResult> GetAsync(string wordId, string? meaningId, AudioLocale locale,
        CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetSnapshotAsync(wordId, meaningId, cancellationToken);
        var selection = AudioSelectionService.Select(snapshot, locale);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.EffectiveBinding == null) return new(selection, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var location = selection.EffectiveBinding.Asset.Location;
            if (await store.StatAsync(location, timeout.Token).WaitAsync(timeout.Token) == null)
                throw new AudioPlaybackUnavailableException();
            timeout.Token.ThrowIfCancellationRequested();
            var link = await store.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(settings.LinkValiditySeconds), timeout.Token)
                .WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return new(selection, link with { ExpiresAt = link.ExpiresAt.ToUniversalTime() });
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // Do not retain provider exceptions, object keys, endpoints or signed URLs.
            throw new AudioPlaybackUnavailableException();
        }
    }
}
