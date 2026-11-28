namespace Dare.Arenas.Core;

/// <summary>
/// The lineup message the zone writes to an arena's bot host (<c>oss-bot --host</c>) on standard input:
/// every bot that should be in the arena, with its one-time pass, seat, team and script. A bot left out
/// (<see cref="BotSeat.Out"/>) is one the host disconnects.
/// </summary>
public static class BotLineup
{
    /// <summary>Longest reply trigger the host takes (oss-bot script.rs).</summary>
    public const int MaxReplyKeyword = 24;

    // The host's own limits (oss-bot script.rs and lineup.rs); a block that breaks one makes it refuse the whole lineup.
    private const int HostMaxPoints = 64, HostMaxReplies = 20;

    /// <summary>
    /// Why the bot host would refuse this bot's block, or null when it would take it. The zone leaves such a
    /// bot out of the lineup: one bad block makes the host refuse the whole message, freezing every bot in the arena.
    /// </summary>
    public static string? HostProblem(ArenaBot bot)
    {
        var name = bot.Label;
        if (name.Length is < 1 or > 20 || !char.IsAsciiLetterOrDigit(name[0]) || name.Contains("  ", StringComparison.Ordinal) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '_' or '-' or '.')))
            return "the bot host cannot use this name";
        int points = 0, replies = 0;
        foreach (var line in ScriptLines(bot.Script))
        {
            if (line.Any(c => c is < ' ' or > '~')) return "a setting has characters the bot host cannot read";
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            var value = parts.ElementAtOrDefault(1) ?? "";
            var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? why = parts[0] switch
            {
                "ship" => int.TryParse(value, out int ship) && ship is >= 1 and <= 8 ? null : "the ship must be 1-8",
                "behaviour" => value is "idle" or "patrol" or "sentry" or "fighter" or "swarm" ? null : "unknown behaviour",
                "point" => ++points > HostMaxPoints ? $"more than {HostMaxPoints} points"
                    : fields.Length == 2 && fields.All(f => int.TryParse(f, out int n) && n is >= 0 and <= 1023) ? null
                    : "a point must be two tiles, 0-1023",
                "range" => int.TryParse(value, out int range) && range is >= 1 and <= 200 ? null : "the range must be 1-200 tiles",
                "greet" => null,
                "reply" => ++replies > HostMaxReplies ? $"more than {HostMaxReplies} replies"
                    : fields.Length >= 1 && fields[0].Length <= MaxReplyKeyword ? null
                    : $"a reply trigger is longer than {MaxReplyKeyword} characters",
                _ => $"the bot host does not know the setting \"{parts[0]}\"",
            };
            if (why is not null) return why;
        }
        return null;
    }

    /// <summary>
    /// The lineup text. A bot the host would refuse (<see cref="HostProblem"/>) is left out, so it cannot stop the rest.
    /// </summary>
    public static string Render(string arena, string controller,
        IReadOnlyList<(ArenaBot Bot, BotPlacement Placement, string Password)> bots)
    {
        var text = new System.Text.StringBuilder();
        text.Append("lineup ").Append(arena).Append('\n');
        text.Append("controller ").Append(controller).Append('\n');
        foreach (var (bot, placement, password) in bots)
        {
            if (placement.Seat == BotSeat.Out || HostProblem(bot) is not null) continue;
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
        lines.RemoveAll(line => line.StartsWith("botchat ", StringComparison.Ordinal) || line.StartsWith("announce ", StringComparison.Ordinal) ||
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
