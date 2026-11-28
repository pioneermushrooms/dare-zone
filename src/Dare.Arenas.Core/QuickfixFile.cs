using System.Text;

namespace Dare.Arenas.Core;

/// <summary>
/// The wire formats of the in-game settings editor (Continuum's, opened by <c>?quickfix</c>, and the
/// Dare browser client's): the <c>server.set</c> file the zone sends, one
/// <c>Section:Key:Value:Min:Max:Help</c> line per setting, and the C2S 0x1D packet the client sends
/// back, NUL-terminated <c>Section:Key:Value</c> strings (plus <c>__pragma:perm:0|1</c>).
/// </summary>
public static class QuickfixFile
{
    /// <summary>One row of server.set.</summary>
    public sealed record Row(string Section, string Key, string Value, string Min, string Max, string Help);

    /// <summary>
    /// One server.set line. False (nothing written) when a field would break the format: a ':' in
    /// the section, key or value, or a line break anywhere. The help may hold ':' (it is last).
    /// </summary>
    public static bool TryLine(Row row, out string line)
    {
        line = "";
        if (HasColon(row.Section) || HasColon(row.Key) || HasColon(row.Value) || HasColon(row.Min) || HasColon(row.Max))
            return false;
        var help = string.Join(' ', row.Help.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = $"{row.Section}:{row.Key}:{row.Value}:{row.Min}:{row.Max}:{help}\r\n";
        return !line[..^2].Contains('\n') && !line[..^2].Contains('\r');
    }

    private static bool HasColon(string s) => s.Contains(':') || s.Contains('\n') || s.Contains('\r');

    /// <summary>A change the client asked for.</summary>
    public sealed record Change(string SectionKey, string Value);

    /// <summary>
    /// Reads the body of a C2S 0x1D packet (the bytes after the type byte). Each change is
    /// <c>Section:Key:Value</c> (the value may be empty and may hold ':'); <c>__pragma</c> entries
    /// set <paramref name="permanent"/> and are not returned. False when an entry is malformed.
    /// </summary>
    public static bool TryParseChanges(ReadOnlySpan<byte> body, Encoding encoding, out List<Change> changes, out bool permanent)
    {
        changes = [];
        permanent = true;
        while (!body.IsEmpty)
        {
            var nul = body.IndexOf((byte)0);
            if (nul == 0) break;
            var entry = encoding.GetString(nul < 0 ? body : body[..nul]);
            body = nul < 0 ? [] : body[(nul + 1)..];

            var first = entry.IndexOf(':');
            var second = first < 0 ? -1 : entry.IndexOf(':', first + 1);
            if (first <= 0 || second <= first + 1) return false;
            var section = entry[..first];
            var key = entry[(first + 1)..second];
            var value = entry[(second + 1)..];

            if (section == "__pragma")
            {
                if (key == "perm") permanent = value != "0";
                continue;
            }
            changes.Add(new Change($"{section}:{key}", value));
        }
        return true;
    }

    /// <summary>Help text longer than this is cut (the editors show a few lines).</summary>
    public const int MaxHelpLength = 240;

    /// <summary>Collapses whitespace and cuts help to <see cref="MaxHelpLength"/>.</summary>
    public static string Shorten(string help)
    {
        var h = string.Join(' ', help.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return h.Length <= MaxHelpLength ? h : h[..(MaxHelpLength - 3)].TrimEnd() + "...";
    }

    /// <summary>
    /// The help of a ship setting after the first ship: a pointer at the first ship's, which the
    /// browser client replaces with that text. Starts with <see cref="SameAsPrefix"/>.
    /// </summary>
    public static string SameAs(string firstShip, string key) => $"{SameAsPrefix}{firstShip}:{key}";

    public const string SameAsPrefix = "As for ";

    /// <summary>
    /// The settings a filter picks, as the server's own ?quickfix does: all of them for a blank
    /// filter, else those whose section or key contains it (ignoring case).
    /// </summary>
    public static bool Matches(string filter, string section, string key) =>
        string.IsNullOrWhiteSpace(filter)
        || section.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || key.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
}
