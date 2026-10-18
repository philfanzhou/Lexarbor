using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

/// <summary>Infrastructure-neutral object operations; never called inside a database write transaction.</summary>
public interface IAudioObjectStore
{
    Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType,
        CancellationToken cancellationToken);
    Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType,
        AudioUploadPurpose purpose, CancellationToken cancellationToken);
    Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken cancellationToken);
    Task RemoveAsync(AudioObjectLocation location, CancellationToken cancellationToken);
    Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity,
        CancellationToken cancellationToken);
}
