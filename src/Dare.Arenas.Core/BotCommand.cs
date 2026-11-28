namespace Dare.Arenas.Core;

/// <summary>
/// Reads the words after <c>?bots create</c> / <c>?bots set</c>: a bot name (which may have spaces),
/// and optionally a behaviour and a ship, in any order around the name. "Sparring fighter 3",
/// "fighter Sparring", "Big Bob ship 2 patrol" and "Sparring [bot]" all read.
/// </summary>
public static class BotCommand
{
    public static readonly string[] Behaviours = ["idle", "patrol", "sentry", "fighter"];

    public static readonly string[] ShipNames =
        ["Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark"];

    public sealed record Args(string Label, string? Behaviour, int? Ship, bool? Chat = null, int? Team = null, int? Priority = null, bool Stopped = false);

    public static Args Parse(string text)
    {
        var words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        string? behaviour = null;
        int? ship = null;
        bool? chat = null;
        int? team = null, priority = null;
        bool stopped = false;

        bool TakeOption(int i)
        {
            var word = words[i].ToLowerInvariant();
            // "stopped": create it without sending it in (the in-game window's Save stopped)
            if (!stopped && word == "stopped" && words.Count > 1)
            {
                stopped = true;
                words.RemoveAt(i);
                return true;
            }
            if (behaviour is null && (Behaviours.Contains(word) || word == "behavior" || word == "behaviour"))
            {
                if (word is "behavior" or "behaviour")
                {
                    if (i + 1 >= words.Count || !Behaviours.Contains(words[i + 1].ToLowerInvariant())) return false;
                    behaviour = words[i + 1].ToLowerInvariant();
                    words.RemoveRange(i, 2);
                    return true;
                }
                behaviour = word;
                words.RemoveAt(i);
                return true;
            }
            if (chat is null && word == "chat" && i + 1 < words.Count &&
                words[i + 1].ToLowerInvariant() is "on" or "off")
            {
                chat = words[i + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
                words.RemoveRange(i, 2);
                return true;
            }
            if (team is null && word == "team" && i + 1 < words.Count &&
                int.TryParse(words[i + 1], out int t) && t is >= 0 and <= 9999)
            {
                team = t;
                words.RemoveRange(i, 2);
                return true;
            }
            if (priority is null && word == "priority" && i + 1 < words.Count &&
                int.TryParse(words[i + 1], out int pr) && pr is >= 1 and <= 9)
            {
                priority = pr;
                words.RemoveRange(i, 2);
                return true;
            }
            if (ship is null && word == "ship" && i + 1 < words.Count && ShipNumber(words[i + 1]) is { } n)
            {
                ship = n;
                words.RemoveRange(i, 2);
                return true;
            }
            if (ship is null && ShipNumber(word) is { } m && words.Count > 1 &&
                !(i > 0 && words[i - 1].Equals("ship", StringComparison.OrdinalIgnoreCase)))
            {
                ship = m;
                words.RemoveAt(i);
                return true;
            }
            return false;
        }

        // options at the back ("Sparring fighter 3") first, so a bot named Patrol keeps its name ...
        for (bool took = true; took && words.Count > 1;)
        {
            took = false;
            for (int i = Math.Max(1, words.Count - 2); i < words.Count; i++)
                if (TakeOption(i)) { took = true; break; }
        }
        // ... then at the front ("fighter Sparring", the old ?bots save order)
        while (words.Count > 1 && TakeOption(0)) { }

        return new(Label(string.Join(' ', words)), behaviour, ship, chat, team, priority, stopped);
    }

    /// <summary>A bot name on its own (deploy, stop, remove): "Sparring" or "Sparring [bot]".</summary>
    public static string Label(string text)
    {
        var label = string.Join(' ', text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        const string suffix = "[bot]";
        return label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? label[..^suffix.Length].TrimEnd() : label;
    }

    private static int? ShipNumber(string word)
    {
        if (int.TryParse(word, out int n) && n is >= 1 and <= 8) return n;
        int index = Array.FindIndex(ShipNames, s => s.Equals(word, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index + 1;
    }

    /// <summary>The script with its behaviour and ship replaced; every other line (waypoints, chat) kept.</summary>
    public static string Apply(string? script, string? behaviour, int? ship, bool? chat = null, int? team = null, int? priority = null)
    {
        var lines = (script ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToList();
        string Current(string key, string fallback) =>
            lines.FirstOrDefault(line => line.StartsWith(key + " ", StringComparison.Ordinal))?[(key.Length + 1)..] ?? fallback;
        var shipLine = "ship " + (ship?.ToString() ?? Current("ship", "1"));
        var behaviourLine = "behaviour " + (behaviour ?? Current("behaviour", "fighter"));
        // team and priority stay as saved unless given; a script without them keeps none (defaults apply)
        var extra = new List<string>();
        foreach (var (key, value) in new[] { ("team", team), ("priority", priority) })
        {
            var current = Current(key, "");
            if (value is not null) extra.Add($"{key} {value}");
            else if (current.Length != 0) extra.Add($"{key} {current}");
        }
        lines.RemoveAll(line => line.StartsWith("ship ", StringComparison.Ordinal) ||
                                line.StartsWith("behaviour ", StringComparison.Ordinal) ||
                                line.StartsWith("team ", StringComparison.Ordinal) ||
                                line.StartsWith("priority ", StringComparison.Ordinal));
        // Casual chat: replies when a pilot names the bot (the web editor's Casual chat switch).
        if (chat == false)
            lines.RemoveAll(line => line.StartsWith("casual ", StringComparison.Ordinal) || line == "botchat on");
        if (chat == true && !lines.Any(line => line.StartsWith("casual ", StringComparison.Ordinal)))
            lines.Add("casual 2 friendly");
        return string.Join('\n', extra.Concat(lines).Prepend(behaviourLine).Prepend(shipLine));
    }

    /// <summary>The script's team (frequency): its <c>team</c> line, 0 when absent or invalid.</summary>
    public static int Team(string script) => Number(script, "team", 0, 9999, 0);

    /// <summary>The script's priority: its <c>priority</c> line, 5 when absent or invalid.</summary>
    public static int Priority(string script) => Number(script, "priority", 1, 9, 5);

    private static int Number(string script, string key, int min, int max, int fallback)
    {
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(key + " ", StringComparison.Ordinal) &&
                int.TryParse(line[(key.Length + 1)..], out int n) && n >= min && n <= max)
                return n;
        }
        return fallback;
    }

    /// <summary>Reads "&lt;team&gt; &lt;size&gt;" or "&lt;team&gt; off" after <c>?bots match</c>; null when it is neither.</summary>
    public static (int Freq, int? Size)? ParseMatch(string text)
    {
        var words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2 || !int.TryParse(words[0], out int freq) || freq is < 0 or > 9999) return null;
        if (words[1].Equals("off", StringComparison.OrdinalIgnoreCase)) return (freq, null);
        return int.TryParse(words[1], out int size) && size >= 0 ? (freq, size) : null;
    }

    /// <summary>"fighter, Warbird, team 2" for a saved script.</summary>
    public static string Describe(string script, bool withTeam = true)
    {
        string? behaviour = null;
        int ship = 1, points = 0;
        bool chat = false, hasTeam = false;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("behaviour ", StringComparison.Ordinal)) behaviour = line[10..];
            else if (line.StartsWith("ship ", StringComparison.Ordinal) && int.TryParse(line[5..], out int n) && n is >= 1 and <= 8) ship = n;
            else if (line.StartsWith("point ", StringComparison.Ordinal)) points++;
            else if (line.StartsWith("casual ", StringComparison.Ordinal)) chat = true;
            else if (line.StartsWith("team ", StringComparison.Ordinal)) hasTeam = true;
        }
        var text = $"{behaviour ?? "idle"}, {ShipNames[ship - 1]}";
        if (hasTeam && withTeam) text += $", team {Team(script)}";
        if (points > 0) text += $", {points} waypoint{(points == 1 ? "" : "s")}";
        return text + (chat ? ", chats" : "");
    }

    /// <summary>A <c>?bots list</c> line: name, behaviour and ship, team and priority once, then the state.</summary>
    public static string ListLine(string label, string script, string state) =>
        $"{label}  {Describe(script, withTeam: false)}  team {Team(script)}, priority {Priority(script)}  [{state}]";

    /// <summary>"not launched: a pilot uses this name" from the store's "Not launched: a pilot uses this name." (case of the reason kept).</summary>
    public static string NotLaunched(string problem)
    {
        const string prefix = "Not launched:";
        var why = (problem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? problem[prefix.Length..] : problem).Trim().TrimEnd('.');
        return "not launched: " + why;
    }
}
