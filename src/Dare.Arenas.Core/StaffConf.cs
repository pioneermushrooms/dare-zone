namespace Dare.Arenas.Core;

/// <summary>Reads player names out of staff.conf: every <c>name = group</c> line of every section except <c>[General]</c> and <c>[GroupPasswords]</c>.</summary>
public static class StaffConf
{
    public static IReadOnlyList<string> Names(string? staffText)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(staffText)) return names;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in staffText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '/') continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                section = end > 0 ? line[1..end].Trim() : "";
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var keySection = section;
            var colon = key.IndexOf(':');
            if (colon >= 0)
            {
                keySection = key[..colon].Trim();
                key = key[(colon + 1)..].Trim();
            }
            if (key.Length == 0 || IsSettingsSection(keySection)) continue;
            if (seen.Add(key)) names.Add(key);
        }
        return names;
    }

    /// <summary>
    /// True when <paramref name="name"/> has a zone-wide staff group: a line in staff.conf's
    /// <c>[(global)]</c> section (or <c>(global):name</c> anywhere). Arena sections don't count.
    /// </summary>
    public static bool IsGlobalStaff(string? staffText, string name)
    {
        if (string.IsNullOrEmpty(staffText) || string.IsNullOrWhiteSpace(name)) return false;
        var section = "";
        foreach (var raw in staffText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '/') continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                section = end > 0 ? line[1..end].Trim() : "";
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var keySection = section;
            var colon = key.IndexOf(':');
            if (colon >= 0)
            {
                keySection = key[..colon].Trim();
                key = key[(colon + 1)..].Trim();
            }
            if (keySection.Equals("(global)", StringComparison.OrdinalIgnoreCase)
                && key.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
                && line[(eq + 1)..].Trim().Length > 0)
                return true;
        }
        return false;
    }

    private static bool IsSettingsSection(string s) =>
        s.Equals("General", StringComparison.OrdinalIgnoreCase) || s.Equals("GroupPasswords", StringComparison.OrdinalIgnoreCase);
}
