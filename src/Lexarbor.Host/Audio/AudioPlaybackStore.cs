using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Host.Audio;

// Domain deadlines may stop waiting before an uncooperative provider ends.
// Each actual call keeps the same client alive past the HTTP operation's disposal.
internal sealed class AudioPlaybackStore(AudioOperationReference operation) : IAudioObjectStore
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
        => PutAsync(location, content, byteLength, contentType, AudioUploadPurpose.Vocabulary, token);
    public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, AudioUploadPurpose purpose, CancellationToken token)
        => throw new NotSupportedException();
    public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new NotSupportedException();
}
