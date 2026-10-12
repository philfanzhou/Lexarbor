using Lexarbor.Domain.Services;

namespace Lexarbor.Service;

public interface IAudioPlaybackOperationFactory
{
    Task<AudioPlaybackRequestOperation> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>One configuration held through authorization, selection and every requested accent.</summary>
public sealed class AudioPlaybackRequestOperation(bool enabled, AudioPlaybackService admin,
    AudioPublicPlaybackService publicPlayback, IDisposable reference) : IDisposable
{
    public bool Enabled { get; } = enabled;
    public AudioPlaybackService Admin { get; } = admin;
    public AudioPublicPlaybackService Public { get; } = publicPlayback;
    public void Dispose() => reference.Dispose();
}
