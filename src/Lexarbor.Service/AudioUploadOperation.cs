using Lexarbor.Domain.Services;

namespace Lexarbor.Service;

public interface IAudioUploadOperationFactory
{
    Task<AudioUploadRequestOperation> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>One complete configuration for HTTP parsing and domain upload.</summary>
public sealed class AudioUploadRequestOperation(AudioUploadHttpSettings settings, AudioUploadService service, IDisposable reference) : IDisposable
{
    public AudioUploadHttpSettings Settings { get; } = settings;
    public AudioUploadService Service { get; } = service;
    public void Dispose() => reference.Dispose();
}
