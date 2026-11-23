namespace Dare.Arenas.Core;

/// <summary>Decides which arena.conf settings an arena owner may edit.</summary>
public sealed class SettingsPolicy
{
    private readonly HashSet<(string, string)> _editable;

    /// <param name="documentedArenaKeys">Keys the server's config help documents with Arena scope.</param>
    public SettingsPolicy(IEnumerable<(string Section, string Key)> documentedArenaKeys)
    {
        _editable = new HashSet<(string, string)>();
        foreach (var (section, key) in documentedArenaKeys)
        {
            if (IsDenied(section, key))
                continue;
            _editable.Add((section.ToLowerInvariant(), key.ToLowerInvariant()));
        }
    }

    public bool IsEditable(string section, string key) =>
        !IsDenied(section, key) && _editable.Contains((section.ToLowerInvariant(), key.ToLowerInvariant()));

    private static bool IsDenied(string section, string key) =>
        section.Equals("Modules", StringComparison.OrdinalIgnoreCase)
        || (section.Equals("General", StringComparison.OrdinalIgnoreCase)
            && (key.Equals("Map", StringComparison.OrdinalIgnoreCase)
                || key.Equals("LevelFiles", StringComparison.OrdinalIgnoreCase)));
}
