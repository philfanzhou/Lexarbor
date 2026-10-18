using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service;

namespace Lexarbor.Host.Audio;

public sealed class TextbookAudioPlaybackOperationFactory(AudioRuntimeConfiguration runtime,
    ITextbookAudioPlaybackReadRepository repository) : ITextbookAudioPlaybackOperationFactory
{
    public async Task<TextbookAudioPlaybackOperation> AcquireAsync(CancellationToken cancellationToken)
    {
        var reference = await runtime.AcquireAsync(cancellationToken);
        var snapshot = reference.Snapshot;
        return new(snapshot.Enabled, new TextbookAudioPlaybackService(repository, new AudioPlaybackStore(reference),
            new(snapshot.PlaybackTtlSeconds, snapshot.OperationTimeoutSeconds)), reference);
    }
}
