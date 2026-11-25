namespace Dare.Arenas.Core;

/// <summary>Whether a pilot may stay in a draft arena they entered.</summary>
public enum EntryDecision
{
    Allow,
    SendToPublic,
}

/// <summary>
/// Rules for drafts: a player's private arena <c>#name</c>, published later as <c>name</c>.
/// The server treats any arena whose name starts with '#' as private (hidden from arena lists).
/// </summary>
public static class DraftRules
{
    public const char Prefix = '#';

    /// <summary>
    /// Longest public name for a draft: the server cuts arena names at 15 characters
    /// (Constants.MaxArenaNameLength) when a player travels, and the draft adds the '#'.
    /// </summary>
    public const int MaxNameLength = 14;

    public static bool IsDraft(string arena) => arena.Length > 0 && arena[0] == Prefix;

    public static string DraftOf(string name) => Prefix + PublicOf(name);

    public static string PublicOf(string arena) => IsDraft(arena) ? arena[1..] : arena;

    /// <summary>
    /// Validates a draft name, given with or without its '#'. <paramref name="existing"/> holds
    /// names already in use, plain or with '#'; a draft and its public name are reserved together.
    /// </summary>
    /// <param name="publicName">The lower-case name without '#' on success, else empty.</param>
    public static bool TryValidate(string input, IReadOnlySet<string> existing, out string publicName, out string error)
    {
        publicName = "";
        var name = PublicOf((input ?? "").Trim());
        if (!ArenaName.TryValidate(name, new HashSet<string>(), out var normalized, out error))
            return false;
        if (normalized.Length > MaxNameLength)
        {
            error = $"Draft names are at most {MaxNameLength} characters (the draft adds a '#').";
            return false;
        }
        if (existing.Any(e => string.Equals(PublicOf(e), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            error = "An arena or draft with that name already exists.";
            return false;
        }
        publicName = normalized;
        return true;
    }

    /// <summary>Owner, co-mods, invited pilots and global staff may stay; anyone else goes to public.</summary>
    public static EntryDecision Entry(string? role, bool invited, bool globalStaff) =>
        role is not null || invited || globalStaff ? EntryDecision.Allow : EntryDecision.SendToPublic;

    /// <summary>
    /// The files a publish (or an approval) copies from a draft: arena.conf, the map named by
    /// General:Map and the graphics named by General:LevelFiles. Unsafe names are left out.
    /// </summary>
    public static IReadOnlyList<string> PublishFiles(string? mapSetting, string? levelFilesSetting)
    {
        var files = new List<string> { "arena.conf" };
        void AddFile(string name)
        {
            if (LevelFiles.IsSafeFileName(name) && !files.Contains(name, StringComparer.OrdinalIgnoreCase))
                files.Add(name);
        }
        var map = (mapSetting ?? "").Trim();
        if (map.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase))
            AddFile(map);
        foreach (var lvz in LevelFiles.Parse(levelFilesSetting))
            if (lvz.EndsWith(".lvz", StringComparison.OrdinalIgnoreCase))
                AddFile(lvz);
        return files;
    }

    /// <summary>
    /// <see cref="PublishFiles"/> from the text of an arena.conf: <c>Map</c> and <c>LevelFiles</c>
    /// in its <c>[General]</c> section (names compared case-insensitively, as the server does).
    /// </summary>
    public static IReadOnlyList<string> PublishFilesFromConf(string confText)
    {
        string? map = null, levelFiles = null;
        var inGeneral = false;
        foreach (var raw in (confText ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                inGeneral = string.Equals(line.Trim('[', ']').Trim(), "General", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // uploads and ?arenaset write "General:Map = x.lvl" (section:key, valid in any section);
            // the server reads those too, and a later line wins
            var colon = key.IndexOf(':');
            if (colon >= 0)
            {
                if (!string.Equals(key[..colon].Trim(), "General", StringComparison.OrdinalIgnoreCase)) continue;
                key = key[(colon + 1)..].Trim();
            }
            else if (!inGeneral) continue;
            if (string.Equals(key, "Map", StringComparison.OrdinalIgnoreCase)) map = value;
            else if (string.Equals(key, "LevelFiles", StringComparison.OrdinalIgnoreCase)) levelFiles = value;
        }
        return PublishFiles(map, levelFiles);
    }

    /// <summary>The map file an upload stores for <paramref name="arena"/>: no '#' in file names.</summary>
    public static string MapFileName(string arena) => PublicOf(arena) + ".lvl";

    /// <summary>Backup folders (named by UTC time, so they sort by age) beyond the newest <paramref name="keep"/>.</summary>
    public static IReadOnlyList<string> BackupsToDelete(IEnumerable<string> backupFolderNames, int keep = 3) =>
        backupFolderNames.OrderByDescending(n => n, StringComparer.Ordinal).Skip(keep)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
}
