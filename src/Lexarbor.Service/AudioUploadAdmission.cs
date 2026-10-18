namespace Lexarbor.Service;

/// <summary>Dedicated bounded admission, shared by all audio uploads in one host.</summary>
public sealed class AudioUploadAdmission(int concurrency, int queueCapacity, TimeSpan waitTimeout)
{
    private readonly SemaphoreSlim _slots = new(concurrency, concurrency);
    private int _waiting;
    public async Task<Permit?> EnterAsync(CancellationToken cancellationToken)
    {
        if (_slots.Wait(0)) return new Permit(_slots);
        if (Interlocked.Increment(ref _waiting) > queueCapacity)
        {
            Interlocked.Decrement(ref _waiting);
            return null;
        }
        try
        {
            if (!await _slots.WaitAsync(waitTimeout, cancellationToken)) return null;
            return new Permit(_slots);
        }
        finally { Interlocked.Decrement(ref _waiting); }
    }
    /// <summary>Request and actual transfer references share one capacity slot.</summary>
    public sealed class Permit : IDisposable
    {
        private sealed class Capacity(SemaphoreSlim semaphore)
        {
            public readonly SemaphoreSlim Semaphore = semaphore;
            public int References = 1;
        }
        private readonly Capacity _capacity;
        private bool _disposed;
        internal Permit(SemaphoreSlim semaphore) : this(new Capacity(semaphore)) { }
        private Permit(Capacity capacity) => _capacity = capacity;
        public IDisposable Retain()
        {
            lock (_capacity)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _capacity.References++;
                return new Permit(_capacity);
            }
        }
        public void Dispose()
        {
            lock (_capacity)
            {
                if (_disposed) return;
                _disposed = true;
                if (--_capacity.References == 0) _capacity.Semaphore.Release();
            }
        }
    }
}
