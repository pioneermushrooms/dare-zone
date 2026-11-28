namespace Dare.Arenas.Core;

/// <summary>
/// Which of a run's bots the zone warps, and where. A bot spawns wherever its freq's spawn puts it and its brain doesn't
/// path round walls, so a cast line with <see cref="CastLine.At"/> has the bot warped there as it takes its seat:
/// on the change from spectator into a ship, and on a freq change within <see cref="Settle"/> of that (oss-bot places
/// its ship afresh on its own freq change, which lands just after the ship change). Never otherwise while it stays in,
/// so a repelled or chasing bot isn't snapped back. A bot already in a ship when a step gives it an
/// <see cref="CastLine.At"/> is warped once if it sat down within <see cref="Settle"/> or was never warped this run.
/// Bots are their index in the run's cast. Not thread-safe: the zone calls it on the mainloop.
/// </summary>
public sealed class TutorialSeats
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly Dictionary<int, DateTimeOffset> _seatedAt = new();
    private readonly HashSet<int> _warped = new();

    /// <summary>The bot's ship or freq changed; <paramref name="line"/> is its cast line now. Where to warp it, or null.</summary>
    public TileSpot? Changed(int bot, bool wasSpec, bool isSpec, bool freqChanged, CastLine? line, DateTimeOffset now)
    {
        if (isSpec)
        {
            _seatedAt.Remove(bot);
            return null;
        }
        if (wasSpec) _seatedAt[bot] = now;
        else if (!freqChanged || !JustSeated(bot, now)) return null;
        return Warp(bot, line);
    }

    /// <summary>A step started and gave the bot <paramref name="line"/>. Where to warp it, or null.</summary>
    public TileSpot? StepStarted(int bot, bool inShip, CastLine? line, DateTimeOffset now) =>
        inShip && (JustSeated(bot, now) || !_warped.Contains(bot)) ? Warp(bot, line) : null;

    private bool JustSeated(int bot, DateTimeOffset now) => _seatedAt.TryGetValue(bot, out var at) && now - at <= Settle;

    private TileSpot? Warp(int bot, CastLine? line)
    {
        if (line?.At is not { } to) return null;
        _warped.Add(bot);
        return to;
    }
}
