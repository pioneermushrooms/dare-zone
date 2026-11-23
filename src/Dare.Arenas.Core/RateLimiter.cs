namespace Dare.Arenas.Core;

/// <summary>Sliding 60-second window per key. Thread-safe.</summary>
public sealed class RateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly int _perMinute;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new();

    public RateLimiter(int perMinute, Func<DateTimeOffset> clock)
    {
        _perMinute = perMinute;
        _clock = clock;
    }

    public bool TryTake(string key)
    {
        lock (_lock)
        {
            var now = _clock();
            var cutoff = now - Window;
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
