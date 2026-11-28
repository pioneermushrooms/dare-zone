namespace Dare.Arenas.Core;

/// <summary>Parses a <c>Section:Key</c> setting reference typed by a player.</summary>
public static class SettingRef
{
    /// <summary>
    /// Trims <paramref name="text"/>; requires exactly one ':' with non-empty parts made only of
    /// ASCII letters, digits and '_'; the key may also have '-' after its first character (the server's
    /// Spawn:Team0-X keys). On failure both outputs are empty.
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
        if (!IsWord(s, dash: false) || !IsWord(k, dash: true)) return false;

        section = s;
        key = k;
        return true;
    }

    private static bool IsWord(string s, bool dash)
    {
        if (s.Length == 0) return false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || (dash && c == '-' && i > 0)))
                return false;
        }
        return true;
    }
}

/// <summary>Validates a setting value typed by a player before it is written to arena.conf.</summary>
public static class SettingValue
{
    public const int MaxLength = 64;

    /// <summary>Chat messages (Misc:GreetMessage, Misc:PeriodicMessage0-9) may be this long.</summary>
    public const int MaxMessageLength = 200;

    /// <summary>
    /// The arena's chat messages: Misc:GreetMessage and Misc:PeriodicMessage0 through 9. They may be
    /// empty (an empty one is off) and longer than other values.
    /// </summary>
    public static bool IsMessage(string section, string key) =>
        section.Equals("Misc", StringComparison.OrdinalIgnoreCase)
        && (key.Equals("GreetMessage", StringComparison.OrdinalIgnoreCase)
            || (key.Length == "PeriodicMessage0".Length
                && key.StartsWith("PeriodicMessage", StringComparison.OrdinalIgnoreCase)
                && char.IsAsciiDigit(key[^1])));

    /// <summary>Trimmed, non-empty, at most 64 chars, no control characters, no ';' and no backslash.</summary>
    public static bool TryValidate(string? raw, out string value, out string error) =>
        TryValidate(raw, MaxLength, allowEmpty: false, out value, out error);

    /// <summary>
    /// As <see cref="TryValidate(string?, out string, out string)"/>, but a message setting
    /// (<see cref="IsMessage"/>) may be empty and up to <see cref="MaxMessageLength"/> chars.
    /// </summary>
    public static bool TryValidate(string section, string key, string? raw, out string value, out string error) =>
        IsMessage(section, key)
            ? TryValidate(raw, MaxMessageLength, allowEmpty: true, out value, out error)
            : TryValidate(raw, MaxLength, allowEmpty: false, out value, out error);

    private static bool TryValidate(string? raw, int maxLength, bool allowEmpty, out string value, out string error)
    {
        value = "";
        error = "";
        var t = raw?.Trim() ?? "";
        if (t.Length == 0 && !allowEmpty) { error = "A value is required."; return false; }
        if (t.Length > maxLength) { error = $"Values are limited to {maxLength} characters."; return false; }
        foreach (var c in t)
        {
            if (char.IsControl(c)) { error = "Values must not contain control characters or line breaks."; return false; }
            if (c == ';') { error = "Values must not contain ';'."; return false; }
            // The conf reader treats a trailing '\' as a line continuation; refuse any backslash.
            if (c == '\\') { error = "Values must not contain '\\'."; return false; }
        }
        value = t;
        return true;
    }
}
