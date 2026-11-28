namespace Dare.Arenas.Core;

/// <summary>
/// Rules for the tutorial's private arenas and the bots that populate them: which arena a run gets,
/// whether the host has room, and how a step's cast becomes a bot lineup.
/// </summary>
public static class TutorialCast
{
    public const string ArenaBase = "#training";

    /// <summary>The lowest <c>#training1..</c> not in <paramref name="inUse"/>.</summary>
    public static string Allocate(IReadOnlySet<string> inUse)
    {
        for (var i = 1; ; i++)
        {
            var name = ArenaBase + i;
            if (!inUse.Contains(name)) return name;
        }
    }

    /// <summary>True for <c>#training</c> followed by digits only (any case); the bare base name is not a tutorial arena.</summary>
    public static bool IsTutorialArena(string name) =>
        name.Length > ArenaBase.Length && name.StartsWith(ArenaBase, StringComparison.OrdinalIgnoreCase) &&
        name.AsSpan(ArenaBase.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>Casts beyond the full-cast limit use their lean (smaller) version.</summary>
    public static bool Lean(int runningFull, int fullCasts) => runningFull >= fullCasts;

    public static bool HasCapacity(int running, int max) => running < max;

    /// <summary>
    /// One bot per cast line, labelled from <paramref name="names"/> in cast order. The target and the swarm are on
    /// team 1 so the player can shoot them; the teammate is on team 0 with the player.
    /// </summary>
    public static IReadOnlyList<(ArenaBot Bot, BotPlacement Placement)> Lineup(
        string arena, IReadOnlyList<CastLine> cast, IReadOnlyList<string> names)
    {
        if (names.Count < cast.Count) throw new ArgumentException($"{cast.Count} bots need names, got {names.Count}", nameof(names));
        var lineup = new List<(ArenaBot, BotPlacement)>(cast.Count);
        for (var i = 0; i < cast.Count; i++)
        {
            var line = cast[i];
            var seat = line.Seat switch
            {
                "in" => BotSeat.In,
                "spec" => BotSeat.Spectating,
                _ => throw new ArgumentException($"unknown seat '{line.Seat}' in {arena}", nameof(cast)),
            };
            var team = line.Role switch
            {
                "target" or "swarm" => 1,
                "teammate" => 0,
                _ => throw new ArgumentException($"unknown role '{line.Role}' in {arena}", nameof(cast)),
            };
            lineup.Add((new ArenaBot(names[i], line.Script, true), new BotPlacement(names[i], seat, team)));
        }
        return lineup;
    }

    /// <summary>Up to <paramref name="count"/> distinct guest-style names that <paramref name="taken"/> says are free.</summary>
    public static IReadOnlyList<string> PickNames(int count, Func<int, int> next, Func<string, bool> taken)
    {
        var names = new List<string>(count);
        while (names.Count < count)
        {
            var name = GuestNames.PickFree(next, n => taken(n) || names.Contains(n));
            if (name is null) break;
            names.Add(name);
        }
        return names;
    }
}
