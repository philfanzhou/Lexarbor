using Lexarbor.Domain.Repositories;
using Lexarbor.Service;

namespace Lexarbor.Host.Audio;

public sealed class TextbookAudioUploadOperationFactory(AudioRuntimeConfiguration runtime,
    ITextbookAudioUploadRepository repository, TimeProvider clock) : ITextbookAudioUploadOperationFactory
{
    public async Task<TextbookAudioUploadRequestOperation> AcquireAsync(CancellationToken cancellationToken)
    {
        var reference = await runtime.AcquireAsync(cancellationToken);
        var snapshot = reference.Snapshot;
        return new(new(snapshot.Enabled, snapshot.MaxTextbookUploadBytes, snapshot.MaxTextbookDurationSeconds, snapshot.OperationTimeoutSeconds),
            new(repository, new AudioUploadOperationFactory.UploadStore(reference),
                new(snapshot.Enabled, snapshot.Bucket, snapshot.KeyPrefix, snapshot.OperationTimeoutSeconds), clock), reference);
    }
}
