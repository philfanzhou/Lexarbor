using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Configuration;
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

/// <summary>
/// Internal configuration generations. The persistent coordinator is the sole publisher;
/// callers hold one reference until their complete operation has actually ended.
/// </summary>
public sealed class AudioRuntimeConfiguration : IDisposable
{
    private readonly object _gate = new();
    private readonly IAudioObjectStoreFactory _factory;
    private readonly AudioRuntimeSnapshot _startup;
    private Generation _current;
    private long _sequence;
    private bool _stopped;

    public AudioRuntimeConfiguration(IOptions<AudioOptions> options, IAudioObjectStoreFactory factory)
    {
        _factory = factory;
        var initial = options.Value.Copy();
        _startup = AudioRuntimeSnapshot.From(initial, 0);
        _current = CreateGeneration(initial);
    }

    public AudioOperationReference Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var generation = _current;
            generation.References++;
            return new(generation.Snapshot, generation.Store, () => Release(generation));
        }
    }

    // For Host settings code only; never serialize this secret-bearing copy.
    internal AudioOptions CopyCurrentOptions()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            return _current.Options.Copy();
        }
    }

    // The coordinator invokes preparation before file commit. Nothing here runs
    // under the reference/publication lock, and there is no remote IO.
    public Task<PreparedConfigurationChange?> PrepareAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Generation? candidate = null;
        try
        {
            var options = new AudioOptions();
            configuration.GetSection(AudioOptions.SectionName).Bind(options);
            if (!options.IsValid() || !SameAdvancedSettings(AudioRuntimeSnapshot.From(options, 0)))
                return Task.FromResult<PreparedConfigurationChange?>(null);
            candidate = CreateGeneration(options);
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = candidate;
            return Task.FromResult<PreparedConfigurationChange?>(new(
                () => Publish(prepared), () => DisposeStore(prepared)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (candidate is not null) DisposeStore(candidate);
            throw;
        }
        catch (Exception)
        {
            if (candidate is not null) DisposeStore(candidate);
            // Never retain SDK/binder exception details, credentials or endpoints.
            return Task.FromResult<PreparedConfigurationChange?>(null);
        }
    }

    private Generation CreateGeneration(AudioOptions options)
    {
        var copy = options.Copy();
        if (!copy.IsValid()) throw new InvalidOperationException(AudioOptions.ConfigurationFailure);
        try
        {
            var store = _factory.Create(copy.Copy());
            return new(copy, AudioRuntimeSnapshot.From(copy, Interlocked.Increment(ref _sequence)), store);
        }
        catch (Exception)
        {
            throw new InvalidOperationException(AudioOptions.ConfigurationFailure);
        }
    }

    private bool SameAdvancedSettings(AudioRuntimeSnapshot candidate) =>
        candidate.PlaybackTtlSeconds == _startup.PlaybackTtlSeconds
        && candidate.MaxUploadBytes == _startup.MaxUploadBytes
        && candidate.MaxConcurrentUploads == _startup.MaxConcurrentUploads
        && candidate.MaxQueuedUploads == _startup.MaxQueuedUploads
        && candidate.OperationTimeoutSeconds == _startup.OperationTimeoutSeconds
        && candidate.RecoveryBatchSize == _startup.RecoveryBatchSize
        && candidate.RecoveryIntervalSeconds == _startup.RecoveryIntervalSeconds
        && candidate.RecoveryLeaseSeconds == _startup.RecoveryLeaseSeconds
        && candidate.RecoveryRetrySeconds == _startup.RecoveryRetrySeconds
        && candidate.RecoveryMaxRetrySeconds == _startup.RecoveryMaxRetrySeconds
        && candidate.RecoveryRecheckSeconds == _startup.RecoveryRecheckSeconds;

    private void Publish(Generation candidate)
    {
        Generation? disposable = null;
        lock (_gate)
        {
            if (_stopped) disposable = candidate;
            else
            {
                var previous = _current;
                _current = candidate;
                previous.Retired = true;
                if (previous.References == 0) disposable = previous;
            }
        }
        // Disposal never holds the short lock or breaks post-commit publication.
        if (disposable is not null) DisposeStore(disposable);
    }

    private void Release(Generation generation)
    {
        var dispose = false;
        lock (_gate)
        {
            generation.References--;
            dispose = generation.Retired && generation.References == 0;
        }
        if (dispose) DisposeStore(generation);
    }

    public void Dispose()
    {
        Generation? disposable = null;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _current.Retired = true;
            if (_current.References == 0) disposable = _current;
        }
        if (disposable is not null) DisposeStore(disposable);
    }

    private static void DisposeStore(Generation generation)
    {
        if (Interlocked.Exchange(ref generation.Disposed, 1) != 0) return;
        try { (generation.Store as IDisposable)?.Dispose(); }
        catch (Exception) { /* No secret-bearing SDK diagnostics or failed publication. */ }
    }

    private sealed class Generation(AudioOptions options, AudioRuntimeSnapshot snapshot, IAudioObjectStore store)
    {
        public AudioOptions Options { get; } = options;
        public AudioRuntimeSnapshot Snapshot { get; } = snapshot;
        public IAudioObjectStore Store { get; } = store;
        public int References { get; set; }
        public bool Retired { get; set; }
        public int Disposed;
    }
}
