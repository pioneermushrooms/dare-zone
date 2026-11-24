namespace Dare.Arenas.Core;

/// <summary>Reads <c>[Section] Key = value</c> from a small INI-style secrets file (server-only, never in the repo).</summary>
public static class SecretsFile
{
    /// <summary>The trimmed value, or null when the section/key is missing or empty. Names are case-insensitive.</summary>
    public static string? Get(string text, string section, string key)
    {
        string? current = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = line[1..^1].Trim();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || !string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(eq + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }
}
