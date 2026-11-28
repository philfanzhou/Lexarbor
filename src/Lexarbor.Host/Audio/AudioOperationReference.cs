using Lexarbor.Domain.Repositories;

namespace Lexarbor.Host.Audio;

/// <summary>Hold once for the entire operation, including all awaited storage calls.</summary>
public sealed class AudioOperationReference : IDisposable
{
    private Action? _release;
    private readonly Func<AudioOperationReference> _retain;
    public AudioRuntimeSnapshot Snapshot { get; }
    public IAudioObjectStore ObjectStore { get; }

    internal AudioOperationReference(AudioRuntimeSnapshot snapshot, IAudioObjectStore objectStore, Action release, Func<AudioOperationReference> retain)
    {
        Snapshot = snapshot;
        ObjectStore = objectStore;
        _release = release;
        _retain = retain;
    }

    internal AudioOperationReference Retain()
    {
        ObjectDisposedException.ThrowIf(_release is null, this);
        return _retain();
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
