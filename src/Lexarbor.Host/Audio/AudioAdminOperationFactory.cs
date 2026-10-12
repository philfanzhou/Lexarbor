using Lexarbor.Service;

namespace Lexarbor.Host.Audio;

public sealed class AudioAdminOperationFactory(AudioRuntimeConfiguration runtime) : IAudioAdminOperationFactory
{
    public async Task<AudioAdminRequestOperation> AcquireAsync(CancellationToken cancellationToken)
    {
        var reference = await runtime.AcquireAsync(cancellationToken);
        return new(reference.Snapshot.Enabled, reference);
    }
}
