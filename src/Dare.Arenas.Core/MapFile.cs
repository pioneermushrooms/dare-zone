namespace Dare.Arenas.Core;

/// <summary>
/// Where the map named by an arena's <c>General:Map</c> is read from for the web editor: the arena's
/// own folder first (an uploaded map), then the server's shared <c>maps/</c> folder (the template's
/// map), as the server looks for it. Only a plain <c>.lvl</c> file name (letters, digits, '.', '_',
/// '-') qualifies; every candidate
/// resolves strictly inside its folder and is never a link.
/// </summary>
public static class MapFile
{
    public const int MaxNameLength = 64;
    public const long MaxReadBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<string> Candidates(string arenasRoot, string arenaName, string mapsRoot, string? mapSetting)
    {
        var name = (mapSetting ?? "").Trim();
        // same shape as graphics names: letters, digits, '.', '_', '-'; not starting with '.'
        if (name.Length > MaxNameLength || !LevelFiles.IsSafeFileName(name)
            || !name.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        var list = new List<string>(2);
        if (ArenaPaths.Resolve(arenasRoot, arenaName, name) is { } own) list.Add(own);
        if (InFolder(mapsRoot, name) is { } shared) list.Add(shared);
        return list;
    }

    private static string? InFolder(string root, string name)
    {
        if (name.IndexOfAny(['/', '\\', '\0']) >= 0 || name.Trim('.').Length == 0
            || Path.IsPathRooted(name) || Path.GetFileName(name) != name)
            return null;
        var dir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(dir, name));
        if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.Ordinal)) return null;
        if (new FileInfo(full).LinkTarget is not null) return null;
        return full;
    }
}
