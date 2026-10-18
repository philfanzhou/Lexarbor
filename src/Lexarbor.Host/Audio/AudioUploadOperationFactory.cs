using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service;

namespace Lexarbor.Host.Audio;

public sealed class AudioUploadOperationFactory(AudioRuntimeConfiguration runtime, IAudioUploadRepository repository,
    TimeProvider clock) : IAudioUploadOperationFactory
{
    public async Task<AudioUploadRequestOperation> AcquireAsync(CancellationToken cancellationToken)
    {
        var reference = await runtime.AcquireAsync(cancellationToken);
        var snapshot = reference.Snapshot;
        return new(new(snapshot.Enabled, snapshot.MaxUploadBytes, snapshot.OperationTimeoutSeconds),
            new(repository, new UploadStore(reference),
                new(snapshot.Enabled, snapshot.Bucket, snapshot.KeyPrefix, snapshot.OperationTimeoutSeconds), clock), reference);
    }

    // The domain deadline can stop waiting for a provider that ignores cancellation.
    // Retain its client until the actual provider call ends, even after the HTTP finally.
    internal sealed class UploadStore(AudioOperationReference operation) : IAudioObjectStore
    {
        public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, CancellationToken cancellationToken)
            => PutAsync(location, content, byteLength, contentType, AudioUploadPurpose.Vocabulary, cancellationToken);
        public async Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, AudioUploadPurpose purpose, CancellationToken cancellationToken)
        {
            using var transfer = operation.Retain();
            await transfer.ObjectStore.PutAsync(location, content, byteLength, contentType, purpose, cancellationToken);
        }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => throw new NotSupportedException();
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) => throw new NotSupportedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => throw new NotSupportedException();
    }
}
