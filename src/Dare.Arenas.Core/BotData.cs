namespace Dare.Arenas.Core;

/// <summary>
/// The bot list as the in-game bots window reads it (<c>?bots data</c>). Each line starts with
/// <see cref="Marker"/> — a tab: the zone delivers it intact but turns tabs in players' chat into
/// '_' (and drops other control characters), so a pilot can't fake a data line — and
/// carries tab-separated fields: a <c>head</c> line (arena, whether the asker owns it), one
/// <c>bot</c> line per bot, one <c>match</c> line per team size, then <c>end</c>.
/// </summary>
public static class BotData
{
    public const string Marker = "\tbots\t";

    public sealed record Row(string Name, string Behaviour, int Ship, int Team, int Priority, bool Chat,
        bool Deployed, string Status, string? Problem);

    public static IReadOnlyList<string> Render(string arena, bool owner, IReadOnlyList<Row> bots,
        IReadOnlyDictionary<int, int> matchSizes)
    {
        var lines = new List<string> { Line("head", arena, owner ? "yes" : "no") };
        foreach (var b in bots)
            lines.Add(Line("bot", b.Name, b.Behaviour, b.Ship.ToString(), b.Team.ToString(), b.Priority.ToString(),
                b.Chat ? "on" : "off", b.Deployed ? "yes" : "no", b.Status, owner ? Clean(b.Problem ?? "") : ""));
        foreach (var (freq, size) in matchSizes.OrderBy(kv => kv.Key))
            lines.Add(Line("match", freq.ToString(), size.ToString()));
        lines.Add(Line("end"));
        return lines;
    }

    /// <summary>A row from a saved bot script, read the way <see cref="BotCommand.Describe"/> reads it.</summary>
    public static Row FromScript(string name, string script, bool deployed, string status, string? problem)
    {
        string behaviour = "idle";
        int ship = 1;
        bool chat = false;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("behaviour ", StringComparison.Ordinal)) behaviour = line[10..];
            else if (line.StartsWith("ship ", StringComparison.Ordinal) && int.TryParse(line[5..], out int n) && n is >= 1 and <= 8) ship = n;
            else if (line.StartsWith("casual ", StringComparison.Ordinal)) chat = true;
        }
        return new(name, behaviour, ship, BotCommand.Team(script), BotCommand.Priority(script), chat, deployed, status, problem);
    }

    private static string Line(params string[] fields) => Marker + string.Join('\t', fields);

    // a field never carries a tab or line break
    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
