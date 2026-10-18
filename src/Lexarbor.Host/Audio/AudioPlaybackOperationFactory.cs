using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service;

namespace Lexarbor.Host.Audio;

public sealed class AudioPlaybackOperationFactory(AudioRuntimeConfiguration runtime, IAudioReadRepository repository,
    IAudioPublicReadRepository publicRepository) : IAudioPlaybackOperationFactory
{
    public async Task<AudioPlaybackRequestOperation> AcquireAsync(CancellationToken cancellationToken)
    {
        var reference = await runtime.AcquireAsync(cancellationToken);
        var snapshot = reference.Snapshot;
        var playback = new AudioPlaybackService(repository, new AudioPlaybackStore(reference),
            new(snapshot.PlaybackTtlSeconds, snapshot.OperationTimeoutSeconds));
        return new(snapshot.Enabled, playback, new(publicRepository, playback), reference);
    }
}
