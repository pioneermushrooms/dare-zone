namespace Dare.Arenas.Core;

/// <summary>Parses a <c>Section:Key</c> setting reference typed by a player.</summary>
public static class SettingRef
{
    /// <summary>
    /// Trims <paramref name="text"/>; requires exactly one ':' with non-empty parts made only of
    /// ASCII letters, digits and '_'. On failure both outputs are empty.
    /// </summary>
    public static bool TryParse(string text, out string section, out string key)
    {
        section = "";
        key = "";
        if (text is null) return false;

        var t = text.Trim();
        var colon = t.IndexOf(':');
        if (colon < 0 || colon != t.LastIndexOf(':')) return false;

        var s = t[..colon];
        var k = t[(colon + 1)..];
        if (!IsWord(s) || !IsWord(k)) return false;

        section = s;
        key = k;
        return true;
    }

    private static bool IsWord(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        return true;
    }
}

/// <summary>Validates a setting value typed by a player before it is written to arena.conf.</summary>
public static class SettingValue
{
    public const int MaxLength = 64;

    /// <summary>Trimmed, non-empty, at most 64 chars, no control characters and no ';'.</summary>
    public static bool TryValidate(string? raw, out string value, out string error)
    {
        value = "";
        error = "";
        var t = raw?.Trim() ?? "";
        if (t.Length == 0) { error = "A value is required."; return false; }
        if (t.Length > MaxLength) { error = $"Values are limited to {MaxLength} characters."; return false; }
        foreach (var c in t)
        {
            if (char.IsControl(c)) { error = "Values must not contain control characters or line breaks."; return false; }
            if (c == ';') { error = "Values must not contain ';'."; return false; }
        }
        value = t;
        return true;
    }
}
