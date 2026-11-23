namespace Dare.Arenas.Core;

/// <summary>Resolves owner-supplied file names to paths strictly inside an arena's directory.</summary>
public static class ArenaPaths
{
    public static string? Resolve(string arenasRoot, string arenaName, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0
            || fileName.Trim('.').Length == 0
            || Path.IsPathRooted(fileName)
            || Path.GetFileName(fileName) != fileName)
            return null;

        var dir = Path.GetFullPath(Path.Combine(arenasRoot, arenaName));
        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        if (!string.Equals(Path.GetDirectoryName(full), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            return null;

        var info = new FileInfo(full);
        if (info.LinkTarget != null)
            return null;
        return full;
    }
}
