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

    public sealed record Args(string Label, string? Behaviour, int? Ship);

    public static Args Parse(string text)
    {
        var words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        string? behaviour = null;
        int? ship = null;

        bool TakeOption(int i)
        {
            var word = words[i].ToLowerInvariant();
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

        return new(Label(string.Join(' ', words)), behaviour, ship);
    }

    /// <summary>A bot name on its own (deploy, stop, remove): "Sparring" or "Sparring [bot]".</summary>
    public static string Label(string text)
    {
        var label = string.Join(' ', text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var suffix = ArenaStore.BotSuffix.Trim();
        return label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? label[..^suffix.Length].TrimEnd() : label;
    }

    private static int? ShipNumber(string word)
    {
        if (int.TryParse(word, out int n) && n is >= 1 and <= 8) return n;
        int index = Array.FindIndex(ShipNames, s => s.Equals(word, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index + 1;
    }

    /// <summary>The script with its behaviour and ship replaced; every other line (waypoints, chat) kept.</summary>
    public static string Apply(string? script, string? behaviour, int? ship)
    {
        var lines = (script ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToList();
        string Current(string key, string fallback) =>
            lines.FirstOrDefault(line => line.StartsWith(key + " ", StringComparison.Ordinal))?[(key.Length + 1)..] ?? fallback;
        var shipLine = "ship " + (ship?.ToString() ?? Current("ship", "1"));
        var behaviourLine = "behaviour " + (behaviour ?? Current("behaviour", "fighter"));
        lines.RemoveAll(line => line.StartsWith("ship ", StringComparison.Ordinal) ||
                                line.StartsWith("behaviour ", StringComparison.Ordinal));
        return string.Join('\n', lines.Prepend(behaviourLine).Prepend(shipLine));
    }

    /// <summary>"fighter, Warbird" for a saved script.</summary>
    public static string Describe(string script)
    {
        string? behaviour = null;
        int ship = 1, points = 0;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("behaviour ", StringComparison.Ordinal)) behaviour = line[10..];
            else if (line.StartsWith("ship ", StringComparison.Ordinal) && int.TryParse(line[5..], out int n) && n is >= 1 and <= 8) ship = n;
            else if (line.StartsWith("point ", StringComparison.Ordinal)) points++;
        }
        var text = $"{behaviour ?? "idle"}, {ShipNames[ship - 1]}";
        return points > 0 ? $"{text}, {points} waypoint{(points == 1 ? "" : "s")}" : text;
    }
}
