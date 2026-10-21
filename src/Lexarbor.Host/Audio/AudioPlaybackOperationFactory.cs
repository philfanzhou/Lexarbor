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
        var playback = new AudioPlaybackService(repository, new PlaybackStore(reference),
            new(snapshot.PlaybackTtlSeconds, snapshot.OperationTimeoutSeconds));
        return new(snapshot.Enabled, playback, new(publicRepository, playback), reference);
    }

    // Domain deadlines may stop waiting before an uncooperative provider ends.
    // Each actual call keeps the same client alive past the HTTP operation's disposal.
    private sealed class PlaybackStore(AudioOperationReference operation) : IAudioObjectStore
    {
        public async Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token)
        {
            using var call = operation.Retain();
            return await call.ObjectStore.StatAsync(location, token);
        }
        public async Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token)
        {
            using var call = operation.Retain();
            return await call.ObjectStore.GetPlaybackLinkAsync(location, validity, token);
        }
        public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, CancellationToken token)
            => throw new NotSupportedException();
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new NotSupportedException();
    }
}
