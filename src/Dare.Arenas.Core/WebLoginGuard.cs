namespace Dare.Arenas.Core;

/// <summary>
/// Web sign-in lockout, kept apart from the game's: 10 wrong passwords for one (name, IP) within
/// 10 minutes refuse that pair for 10 minutes. It applies to every typed name, existing or not,
/// so a lock says nothing about which names exist. In memory; thread-safe.
/// </summary>
public sealed class WebLoginGuard
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(10);

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public DateTimeOffset WindowStart;
        public int Count;
        public DateTimeOffset? LockedUntil;
    }

    public WebLoginGuard(Func<DateTimeOffset> clock) => _clock = clock;

    private static string Key(string name, string ip)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        if (n.Length > 64) n = n[..64];
        return n + "|" + ip;
    }

    public bool IsLocked(string name, string ip)
    {
        lock (_lock)
            return _entries.TryGetValue(Key(name, ip), out var e) && e.LockedUntil is { } until && until > _clock();
    }

    public void Failure(string name, string ip)
    {
        lock (_lock)
        {
            var now = _clock();
            Prune(now);
            var key = Key(name, ip);
            if (!_entries.TryGetValue(key, out var e)) _entries[key] = e = new Entry { WindowStart = now };
            if (e.WindowStart + Window <= now) { e.WindowStart = now; e.Count = 0; }
            if (++e.Count >= MaxFailures)
            {
                e.LockedUntil = now + LockFor;
                e.WindowStart = now;
                e.Count = 0;
            }
        }
    }

    public void Success(string name, string ip)
    {
        lock (_lock) _entries.Remove(Key(name, ip));
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count < 256) return;
        foreach (var k in _entries.Where(p => p.Value.WindowStart + Window <= now && (p.Value.LockedUntil is null || p.Value.LockedUntil <= now))
                                  .Select(p => p.Key).ToList())
            _entries.Remove(k);
    }
}
