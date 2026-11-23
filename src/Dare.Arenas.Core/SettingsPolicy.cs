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

    // General keys never editable by owners:
    //   Map, LevelFiles  - changed only through uploads;
    //   ScoreGroup       - names a score group shared with other arenas (cross-arena scores/resets);
    //   NeedCap          - names a zone-wide capability.
    private static readonly HashSet<string> DeniedGeneralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Map", "LevelFiles", "ScoreGroup", "NeedCap",
    };

    private static bool IsDenied(string section, string key) =>
        section.Equals("Modules", StringComparison.OrdinalIgnoreCase)
        || (section.Equals("General", StringComparison.OrdinalIgnoreCase) && DeniedGeneralKeys.Contains(key));
}
