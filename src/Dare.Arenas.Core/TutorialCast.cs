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

    /// <summary>
    /// Every bot a run needs over all its steps, all spectating: per role, as many as the step that uses most of
    /// that role (a step's cast is the whole set wanted then). With <paramref name="shareRoles"/> (lean casts) the
    /// bots are shared between roles: as many as the step with the most lines. Each takes the first line seen for it.
    /// </summary>
    public static IReadOnlyList<CastLine> WholeCast(IEnumerable<IReadOnlyList<CastLine>> casts, bool shareRoles)
    {
        var slots = new List<CastLine>();
        foreach (var cast in casts)
        {
            for (var i = 0; i < cast.Count; i++)
            {
                var line = cast[i];
                var nth = shareRoles ? i : Nth(cast, i);
                var have = shareRoles ? slots.Count : slots.Count(s => s.Role == line.Role);
                if (nth < have) continue;
                var spec = line with { Seat = "spec" };
                // a new slot of a role goes after that role's others, so a role's bots stay together
                var after = shareRoles ? -1 : slots.FindLastIndex(s => s.Role == line.Role);
                if (after < 0) slots.Add(spec);
                else slots.Insert(after + 1, spec);
            }
        }
        return slots;
    }

    /// <summary>
    /// The <see cref="WholeCast"/> as one step wants it: each bot the step uses takes that step's line (seat, script and,
    /// when shared, role); the rest spectate as they are. Same order as the whole cast, so names stay with their bots.
    /// </summary>
    public static IReadOnlyList<CastLine> ForStep(IReadOnlyList<CastLine> whole, IReadOnlyList<CastLine> step, bool shareRoles)
    {
        var now = new List<CastLine>(whole.Count);
        for (var i = 0; i < whole.Count; i++)
        {
            var slot = whole[i];
            CastLine? line = shareRoles
                ? i < step.Count ? step[i] : null
                : step.Where(c => c.Role == slot.Role).ElementAtOrDefault(Nth(whole, i));
            now.Add(line ?? slot with { Seat = "spec" });
        }
        return now;
    }

    // How many lines of the same role come before cast[i].
    private static int Nth(IReadOnlyList<CastLine> cast, int i) => cast.Take(i).Count(c => c.Role == cast[i].Role);

    /// <summary>
    /// The lineup text for a tutorial's bot host. It names no controller: oss-bot would otherwise greet the arena with
    /// how to give it orders (giving the bots away), and whoever holds that name could order them about.
    /// </summary>
    public static string RenderLineup(string arena, IReadOnlyList<(ArenaBot Bot, BotPlacement Placement, string Password)> bots) =>
        BotLineup.Render(arena, null, bots);

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
