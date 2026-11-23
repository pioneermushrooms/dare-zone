namespace Dare.Arenas.Core;

/// <summary>
/// Decides whether a setting can be changed in place in an arena's own arena.conf.
/// The server's in-place update edits the line where the setting is currently defined; that is
/// only safe when this file provably holds the effective definition. Otherwise callers append an
/// override to the end of arena.conf (always safe, but adds a line).
/// </summary>
public static class ConfOverride
{
    /// <summary>
    /// True when <paramref name="confText"/> (the arena.conf on disk) has an active
    /// <c>Section:Key = value</c> line and no preprocessor line (<c>#...</c> / <c>%...</c>) after the
    /// first such line, so nothing included or conditional can redefine it later.
    /// </summary>
    public static bool CanUpdateInPlace(string? confText, string section, string key)
    {
        if (string.IsNullOrEmpty(confText)) return false;
        var name = section + ":" + key;
        var found = false;
        foreach (var raw in confText.Split('\n'))
        {
            var line = raw.Trim();
            if (found && (line.StartsWith('#') || line.StartsWith('%')))
                return false;
            if (!found && IsOverrideOf(line, name))
                found = true;
        }
        return found;
    }

    private static bool IsOverrideOf(string line, string name)
    {
        var eq = line.IndexOf('=');
        var left = eq < 0 ? line : line[..eq];
        return left.Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}
