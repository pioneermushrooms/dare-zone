using System.Globalization;

namespace Dare.Arenas.Core;

/// <summary>Flag:FlagMode, as the server's FlagGamePoints reads it (names or -1/0/1).</summary>
public enum FlagWinMode
{
    None = -1,
    /// <summary>A team wins by carrying every flag (running/jackpot).</summary>
    CarryAll = 0,
    /// <summary>A team wins by owning every flag dropped on the map (warzone).</summary>
    OwnAllDropped = 1,
}

/// <summary>Where a flag is: on the map (owned by Freq, or -1 for nobody) or carried by a pilot on Freq.</summary>
public enum FlagSpot { None, OnMap, Carried }

public readonly record struct FlagView(FlagSpot Spot, short Freq);

/// <summary>
/// The flag win for game-mode arenas. The server's FlagGamePoints awards the win the moment one team has every
/// flag and ignores Flag:WinDelay (CarryFlags reads it, nothing uses it), so a hold time never worked. The
/// plugin runs this rule instead when GameMode:Attach has Flags: the team must keep every flag for WinDelay.
/// </summary>
public static class FlagHold
{
    /// <summary>The name GameMode:Attach records for the plugin's flag rule (it is not a server module).</summary>
    public const string TypeName = "Dare.Arenas.FlagHold";

    /// <summary>The team that has every flag the way <paramref name="mode"/> asks, or null.</summary>
    public static short? Holder(FlagWinMode mode, IReadOnlyList<FlagView> flags)
    {
        if (mode == FlagWinMode.None || flags.Count == 0) return null;
        var want = mode == FlagWinMode.CarryAll ? FlagSpot.Carried : FlagSpot.OnMap;
        var freq = flags[0].Freq;
        if (freq < 0) return null;
        foreach (var f in flags)
            if (f.Spot != want || f.Freq != freq) return null;
        return freq;
    }

    /// <summary>The team with the most flags (carried or owned on the map), or null for a tie or no flags owned.</summary>
    public static short? MostFlags(IReadOnlyList<FlagView> flags)
    {
        var counts = new Dictionary<short, int>();
        foreach (var f in flags)
            if (f.Spot != FlagSpot.None && f.Freq >= 0) counts[f.Freq] = counts.GetValueOrDefault(f.Freq) + 1;
        if (counts.Count == 0) return null;
        var best = counts.Max(kv => kv.Value);
        var top = counts.Where(kv => kv.Value == best).ToList();
        return top.Count == 1 ? top[0].Key : null;
    }

    public static FlagWinMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return FlagWinMode.None;
        var text = value.Trim();
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            return Enum.IsDefined((FlagWinMode)n) ? (FlagWinMode)n : FlagWinMode.None;
        return Enum.TryParse<FlagWinMode>(text, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : FlagWinMode.None;
    }

    /// <summary>
    /// A flag win's points: <paramref name="reward"/> as written (<paramref name="flat"/>, GameMode:FlatFlagReward) or
    /// scaled like the server, (players)^2 * reward / 1000; then the jackpot; then split between the team if asked.
    /// </summary>
    public static int Points(int playing, int onFreq, int reward, bool flat, bool split, int jackpot)
    {
        var points = flat ? reward : (int)(playing * (long)playing * reward / 1000);
        points += jackpot;
        if (split && onFreq > 0) points /= onFreq;
        return points;
    }

    /// <summary>"6 minutes", "2:30", "30 seconds".</summary>
    public static string Say(TimeSpan t)
    {
        var s = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        if (s >= 60 && s % 60 == 0) return s == 60 ? "1 minute" : $"{s / 60} minutes";
        if (s >= 60) return $"{s / 60}:{s % 60:00}";
        return s == 1 ? "1 second" : $"{s} seconds";
    }
}

public enum FlagHoldStep { None, Started, Cancelled, Warn, Won }

/// <summary>What one update did. Previous: the team whose countdown a new holder replaced.</summary>
public readonly record struct FlagHoldEvent(FlagHoldStep Step, short Freq, TimeSpan Remaining, short? Previous = null);

/// <summary>One arena's hold countdown. Feed it the current holder on every flag change and once a second.</summary>
public sealed class FlagHoldClock
{
    private DateTime _endsAt;
    private TimeSpan _nextWarn;

    public short? Freq { get; private set; }

    public void Reset() => Freq = null;

    public FlagHoldEvent Update(short? holder, DateTime now, TimeSpan delay)
    {
        if (holder is not { } h)
        {
            if (Freq is not { } was) return default;
            Freq = null;
            return new(FlagHoldStep.Cancelled, was, TimeSpan.Zero);
        }
        if (Freq == h)
        {
            var left = _endsAt - now;
            if (left <= TimeSpan.Zero) { Freq = null; return new(FlagHoldStep.Won, h, TimeSpan.Zero); }
            if (left > _nextWarn) return default;
            var mark = _nextWarn;
            while (_nextWarn >= left && _nextWarn > TimeSpan.Zero) _nextWarn = NextMark(_nextWarn);
            return new(FlagHoldStep.Warn, h, mark);
        }
        var previous = Freq;
        if (delay <= TimeSpan.Zero) { Freq = null; return new(FlagHoldStep.Won, h, TimeSpan.Zero, previous); }
        Freq = h;
        _endsAt = now + delay;
        _nextWarn = NextMark(delay);
        return new(FlagHoldStep.Started, h, delay, previous);
    }

    /// <summary>The next warning below <paramref name="t"/>: each whole minute, then 30 and 10 seconds.</summary>
    private static TimeSpan NextMark(TimeSpan t)
    {
        var s = (int)Math.Ceiling(t.TotalSeconds);
        if (s > 60) return TimeSpan.FromSeconds((s - 1) / 60 * 60);
        if (s > 30) return TimeSpan.FromSeconds(30);
        if (s > 10) return TimeSpan.FromSeconds(10);
        return TimeSpan.Zero;
    }
}

/// <summary>
/// King of the Hill paid as written (GameMode:KingReward): the arena's King:RewardFactor is 0, so the server's Koth
/// pays nothing, and the plugin pays the winners Koth found when its game ends (KothEndedCallback has no winners).
/// </summary>
public static class KingWin
{
    /// <summary>
    /// Koth's winners: the crowned pilots, if they are one team; or, when no one has a crown, the pilots whose crowns
    /// ran out together just before the end (<paramref name="justLost"/>), if they are one team.
    /// </summary>
    public static IReadOnlyList<T> Winners<T>(IReadOnlyList<(T Pilot, short Freq)> crowned, IReadOnlyList<(T Pilot, short Freq)> justLost)
    {
        var set = crowned.Count > 0 ? crowned : justLost;
        if (set.Count == 0 || set.Any(p => p.Freq != set[0].Freq)) return [];
        return set.Select(p => p.Pilot).ToList();
    }
}
