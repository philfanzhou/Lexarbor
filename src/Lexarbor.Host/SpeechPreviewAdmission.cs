namespace Lexarbor.Host;

/// <summary>Host-wide preview budget, independent of background task failure accounting.</summary>
public sealed class SpeechPreviewAdmission(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _starts = new();
    private bool _active;

    public IDisposable? Acquire(out int retrySeconds)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            while (_starts.TryPeek(out var started) && now - started >= TimeSpan.FromMinutes(1)) _starts.Dequeue();
            retrySeconds = _starts.Count >= 6 ? Math.Max(1, (int)Math.Ceiling((_starts.Peek().AddMinutes(1) - now).TotalSeconds)) : 1;
            if (_active || _starts.Count >= 6) return null;
            _active = true;
            _starts.Enqueue(now);
            return new Lease(this);
        }
    }

    private sealed class Lease(SpeechPreviewAdmission owner) : IDisposable
    {
        private SpeechPreviewAdmission? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } current) return;
            lock (current._gate) current._active = false;
        }
    }
}
