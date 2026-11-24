namespace Dare.Arenas.Core;

/// <summary>Sliding window per key (60 seconds unless given). Thread-safe.</summary>
public sealed class RateLimiter
{
    private readonly TimeSpan _window;
    private readonly int _perMinute;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new();

    public RateLimiter(int perMinute, Func<DateTimeOffset> clock) : this(perMinute, TimeSpan.FromSeconds(60), clock) { }

    /// <summary>At most <paramref name="limit"/> hits per key within any <paramref name="window"/>.</summary>
    public RateLimiter(int limit, TimeSpan window, Func<DateTimeOffset> clock)
    {
        _perMinute = limit;
        _window = window;
        _clock = clock;
    }

    public bool TryTake(string key)
    {
        lock (_lock)
        {
            var now = _clock();
            var cutoff = now - _window;
            foreach (var k in _hits.Where(p => p.Value.Count == 0 || p.Value.All(t => t <= cutoff))
                                   .Select(p => p.Key).ToList())
                _hits.Remove(k);

            if (!_hits.TryGetValue(key, out var q)) _hits[key] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
            if (q.Count >= _perMinute) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
