using Lexarbor.Domain.Services;

namespace Lexarbor.Service;

public interface ITextbookAudioPlaybackOperationFactory
{
    Task<TextbookAudioPlaybackOperation> AcquireAsync(CancellationToken cancellationToken);
}

public sealed class TextbookAudioPlaybackOperation(bool enabled, TextbookAudioPlaybackService playback, IDisposable reference) : IDisposable
{
    public bool Enabled { get; } = enabled;
    public TextbookAudioPlaybackService Playback { get; } = playback;
    public void Dispose() => reference.Dispose();
}
