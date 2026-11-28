using Lexarbor.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Audio;

/// <summary>Construction is local only: do not probe, list or create remote resources.</summary>
public interface IAudioObjectStoreFactory
{
    IAudioObjectStore Create(AudioOptions options);
}

public sealed class S3AudioObjectStoreFactory(TimeProvider time) : IAudioObjectStoreFactory
{
    public IAudioObjectStore Create(AudioOptions options) => new S3AudioObjectStore(Options.Create(options), time);
}
