namespace Lexarbor.Service;

/// <summary>Dedicated bounded admission, shared by all audio uploads in one host.</summary>
public sealed class AudioUploadAdmission(int concurrency, int queueCapacity, TimeSpan waitTimeout)
{
    private readonly SemaphoreSlim _slots = new(concurrency, concurrency);
    private int _waiting;
    public async Task<IDisposable?> EnterAsync(CancellationToken cancellationToken)
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
    private sealed class Permit(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release(); }
    }
}
