namespace Dare.Arenas.Core;

/// <summary>
/// The lineup message the zone writes to an arena's bot host (<c>oss-bot --host</c>) on standard input:
/// every bot that should be in the arena, with its one-time pass, seat, team and script. A bot left out
/// (<see cref="BotSeat.Out"/>) is one the host disconnects.
/// </summary>
public static class BotLineup
{
    public static string Render(string arena, string controller,
        IReadOnlyList<(ArenaBot Bot, BotPlacement Placement, string Password)> bots)
    {
        var text = new System.Text.StringBuilder();
        text.Append("lineup ").Append(arena).Append('\n');
        text.Append("controller ").Append(controller).Append('\n');
        foreach (var (bot, placement, password) in bots)
        {
            if (placement.Seat == BotSeat.Out) continue;
            text.Append("bot ").Append(bot.Label).Append('\n');
            text.Append("password ").Append(password).Append('\n');
            text.Append("seat ").Append(placement.Seat == BotSeat.In ? "in" : "spec").Append('\n');
            text.Append("team ").Append(placement.Team).Append('\n');
            foreach (var line in ScriptLines(bot.Script)) text.Append(line).Append('\n');
            text.Append("end\n");
        }
        text.Append("done\n");
        return text.ToString();
    }

    /// <summary>
    /// The saved script as the host reads it: zone-only lines (team, priority, botchat, announce) left out,
    /// ship 1 when it names none, and a casual chat profile turned into a greeting unless it has one.
    /// </summary>
    public static IReadOnlyList<string> ScriptLines(string script)
    {
        var lines = script.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToList();
        lines.RemoveAll(line => line == "botchat on" || line.StartsWith("announce ", StringComparison.Ordinal) ||
            line.StartsWith("team ", StringComparison.Ordinal) || line.StartsWith("priority ", StringComparison.Ordinal));
        if (!lines.Any(line => line.StartsWith("ship ", StringComparison.Ordinal))) lines.Insert(0, "ship 1");
        var casual = lines.FindIndex(line => line.StartsWith("casual ", StringComparison.Ordinal));
        if (casual < 0) return lines;

        var fields = lines[casual].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lines.RemoveAt(casual);
        var greeting = fields.ElementAtOrDefault(2) switch
        {
            "dry" => "Oh, hello {name}.",
            "competitive" => "You're up, {name}.",
            _ => "Hey {name}, good to see you!"
        };
        if (!lines.Any(line => line.StartsWith("greet ", StringComparison.Ordinal)))
            lines.Add("greet " + greeting);
        return lines;
    }
}

/// <summary>
/// Spreads seat changes that <see cref="BotSeats"/> gives several bots in the same tick over 0–6 s each,
/// so bots never all leave or arrive in the same second. A lone change goes through at once.
/// </summary>
public sealed class BotStagger(Func<DateTimeOffset> clock, Random random)
{
    private sealed class State
    {
        public required BotPlacement Shown;
        public DateTimeOffset? NotBefore;
    }

    private readonly Dictionary<string, State> _bots = new(StringComparer.Ordinal);

    public IReadOnlyList<BotPlacement> Apply(IReadOnlyList<BotPlacement> step)
    {
        var now = clock();
        var seen = step.Select(p => p.Label).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _bots.Keys.Where(k => !seen.Contains(k)).ToList()) _bots.Remove(gone);

        var fresh = new List<State>();
        foreach (var p in step)
        {
            if (!_bots.TryGetValue(p.Label, out var st))
            {
                _bots[p.Label] = new State { Shown = p };
                continue;
            }
            if (st.Shown == p) st.NotBefore = null;
            else if (st.NotBefore is null) fresh.Add(st);
        }
        foreach (var st in fresh)
            st.NotBefore = fresh.Count > 1 ? now + TimeSpan.FromSeconds(random.Next(0, 7)) : now;

        var output = new List<BotPlacement>(step.Count);
        foreach (var p in step)
        {
            var st = _bots[p.Label];
            if (st.NotBefore is { } due && now >= due)
            {
                st.Shown = p;
                st.NotBefore = null;
            }
            output.Add(st.Shown);
        }
        return output;
    }
}
