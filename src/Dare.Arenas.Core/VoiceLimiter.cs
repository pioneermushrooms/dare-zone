namespace Dare.Arenas.Core;

/// <summary>At most <see cref="Burst"/> voice lines within <see cref="Window"/>; one more silences voice for <see cref="Silence"/>.</summary>
public readonly record struct VoiceLimits(int Burst, TimeSpan Window, TimeSpan Silence)
{
    public static VoiceLimits Default => new(3, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10));
}

public enum VoiceVerdictKind
{
    Allowed,
    /// <summary>This attempt tripped the limit; the caller announces the silence.</summary>
    SilencedNow,
    /// <summary>Already silenced; dropped quietly (the client shows the countdown).</summary>
    Silenced,
}

public readonly record struct VoiceVerdict(VoiceVerdictKind Kind, int SecondsLeft);

/// <summary>
/// Per-player voice spam limit with a forced silence. Attempts during a silence neither count
/// nor extend it. Thread-safe; idle players are forgotten on the next call.
/// </summary>
public sealed class VoiceLimiter
{
    private sealed class State
    {
        public readonly Queue<DateTimeOffset> Sent = new();
        public DateTimeOffset SilencedUntil;
    }

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _players = new(StringComparer.OrdinalIgnoreCase);

    public VoiceLimiter(Func<DateTimeOffset> clock) => _clock = clock;

    public VoiceVerdict Try(string key, VoiceLimits limits)
    {
        lock (_lock)
        {
            var now = _clock();
            var cutoff = now - limits.Window;
            foreach (var gone in _players.Where(p => p.Value.SilencedUntil <= now && p.Value.Sent.All(t => t <= cutoff))
                                         .Select(p => p.Key).ToList())
                _players.Remove(gone);

            if (!_players.TryGetValue(key, out var s)) _players[key] = s = new State();
            if (s.SilencedUntil > now)
                return new VoiceVerdict(VoiceVerdictKind.Silenced, SecondsUntil(s.SilencedUntil, now));

            while (s.Sent.Count > 0 && s.Sent.Peek() <= cutoff) s.Sent.Dequeue();
            if (s.Sent.Count < limits.Burst)
            {
                s.Sent.Enqueue(now);
                return new VoiceVerdict(VoiceVerdictKind.Allowed, 0);
            }
            s.Sent.Clear();
            s.SilencedUntil = now + limits.Silence;
            return new VoiceVerdict(VoiceVerdictKind.SilencedNow, SecondsUntil(s.SilencedUntil, now));
        }
    }

    private static int SecondsUntil(DateTimeOffset until, DateTimeOffset now) =>
        (int)Math.Ceiling((until - now).TotalSeconds);
}
