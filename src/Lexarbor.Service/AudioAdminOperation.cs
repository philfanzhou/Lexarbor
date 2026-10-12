namespace Lexarbor.Service;

public interface IAudioAdminOperationFactory
{
    Task<AudioAdminRequestOperation> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>One runtime decision held through the complete metadata query or conditional unbind.</summary>
public sealed class AudioAdminRequestOperation(bool enabled, IDisposable reference) : IDisposable
{
    public bool Enabled { get; } = enabled;
    public void Dispose() => reference.Dispose();
}
