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
    private long _lastEnabledGeneration;
    private bool _stopped;
    private int _activeReferences;
    private TaskCompletionSource? _destinationPending;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IAudioDestinationRecords? _records;

    public AudioRuntimeConfiguration(IOptions<AudioOptions> options, IAudioObjectStoreFactory factory, IAudioDestinationRecords? records = null)
    {
        _factory = factory;
        _records = records;
        var initial = options.Value.Copy();
        _startup = AudioRuntimeSnapshot.From(initial, 0);
        _current = CreateGeneration(initial);
        _lastEnabledGeneration = initial.Enabled ? _current.Snapshot.Generation : 0;
    }

    public AudioOperationReference Acquire() => AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<AudioOperationReference> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task pending;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (_destinationPending is null) return Reference(_current);
                pending = _destinationPending.Task;
            }
            await pending.WaitAsync(cancellationToken);
        }
    }

    // Observe and subscribe under the same short lock, so publication cannot be lost.
    internal Task<AudioRuntimeSnapshot> WaitForChangeAsync(long generation, CancellationToken cancellationToken)
        => WaitAsync(generation, false, cancellationToken);

    // Remember enable transitions even if disable/re-enable coalesces before observation.
    internal Task<AudioRuntimeSnapshot> WaitForEnableAsync(long generation, CancellationToken cancellationToken)
        => WaitAsync(generation, true, cancellationToken);

    private async Task<AudioRuntimeSnapshot> WaitAsync(long generation, bool enabledOnly, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (enabledOnly ? _lastEnabledGeneration > generation : _current.Snapshot.Generation != generation) return _current.Snapshot;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    // Called only while the parent operation owns a reference. No new-generation lookup.
    private AudioOperationReference Reference(Generation generation)
    {
        generation.References++;
        _activeReferences++;
        return new(generation.Snapshot, generation.Store, () => Release(generation), () =>
        {
            lock (_gate) return Reference(generation);
        });
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
    public async Task<PreparedConfigurationChange?> PrepareAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Generation? candidate = null;
        TaskCompletionSource? reservation = null;
        try
        {
            var options = new AudioOptions();
            configuration.GetSection(AudioOptions.SectionName).Bind(options);
            if (!options.IsValid() || !SameAdvancedSettings(AudioRuntimeSnapshot.From(options, 0))) return null;
            candidate = CreateGeneration(options);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (!SameDestination(_current.Snapshot, candidate.Snapshot))
                {
                    if (_activeReferences != 0 || _destinationPending is not null)
                        throw new ConfigurationChangeConflictException();
                    reservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _destinationPending = reservation;
                }
            }
            // The reservation blocks new references; the short state lock is released.
            // No SQLite transaction or remote storage call is part of this check.
            if (reservation is not null && (_records is null || await _records.HasAnyAsync(cancellationToken)))
                throw new ConfigurationChangeConflictException();
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = candidate;
            var pending = reservation;
            return new(() =>
            {
                try { Publish(prepared, pending); }
                finally { EndDestinationChange(pending); }
            }, () =>
            {
                EndDestinationChange(pending);
                DisposeStore(prepared);
            });
        }
        catch (ConfigurationChangeConflictException)
        {
            EndDestinationChange(reservation);
            if (candidate is not null) DisposeStore(candidate);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EndDestinationChange(reservation);
            if (candidate is not null) DisposeStore(candidate);
            throw;
        }
        catch (Exception)
        {
            EndDestinationChange(reservation);
            if (candidate is not null) DisposeStore(candidate);
            // Never retain SDK/binder exception details, credentials or endpoints.
            return null;
        }
    }

    private static bool SameDestination(AudioRuntimeSnapshot left, AudioRuntimeSnapshot right) =>
        left.InternalEndpoint == right.InternalEndpoint && left.Bucket == right.Bucket && left.KeyPrefix == right.KeyPrefix;

    private void EndDestinationChange(TaskCompletionSource? reservation)
    {
        if (reservation is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_destinationPending, reservation)) _destinationPending = null;
        }
        reservation.TrySetResult();
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

    private void Publish(Generation candidate, TaskCompletionSource? reservation = null)
    {
        Generation? disposable = null;
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            if (_stopped) disposable = candidate;
            else
            {
                var previous = _current;
                _current = candidate;
                if (!previous.Snapshot.Enabled && candidate.Snapshot.Enabled) _lastEnabledGeneration = candidate.Snapshot.Generation;
                changed = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                previous.Retired = true;
                if (previous.References == 0) disposable = previous;
            }
        }
        changed?.TrySetResult();
        EndDestinationChange(reservation);
        // Disposal never holds the short lock or breaks post-commit publication.
        if (disposable is not null) DisposeStore(disposable);
    }

    private void Release(Generation generation)
    {
        var dispose = false;
        lock (_gate)
        {
            generation.References--;
            _activeReferences--;
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
        _changed.TrySetResult();
        _destinationPending?.TrySetResult();
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
