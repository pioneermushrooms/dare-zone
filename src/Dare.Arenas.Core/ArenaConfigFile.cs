namespace Dare.Arenas.Core;

/// <summary>
/// Which file an owner edit (setting, map, graphics list) is written to. Always the arena's own
/// <c>arenas/&lt;name&gt;/arena.conf</c>, and only when it exists: the server's config search path
/// would otherwise fall back to <c>conf/arena.conf</c>, <c>./arena.conf</c> or
/// <c>arenas/(default)/arena.conf</c>, which every arena without its own file shares, so a write
/// there would change the whole zone.
/// </summary>
public static class ArenaConfigFile
{
    public const string FileName = "arena.conf";
    private const int MaxNameLength = 32;

    /// <summary>
    /// The full path of <c>arenas/&lt;arenaName&gt;/arena.conf</c> if it exists as a regular file (not a
    /// symlink), else null. Never a fallback. <paramref name="arenaName"/> must be a plain arena
    /// name (a-z, 0-9 and '-', starting with a letter or digit), optionally a draft's leading '#';
    /// anything else has no target.
    /// </summary>
    public static string? OwnPath(string arenasRoot, string arenaName)
    {
        if (!IsPlainName(arenaName)) return null;
        var path = ArenaPaths.Resolve(arenasRoot, arenaName, FileName); // refuses a symlinked file
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// True when the running arena already sees <paramref name="written"/>, i.e. its config is the
    /// file that was edited. Otherwise it runs on a shared file and the change applies after
    /// <c>?recyclearena</c>.
    /// </summary>
    public static bool IsLive(string? runningValue, string written) =>
        string.Equals(written.Length == 0 ? runningValue ?? "" : runningValue, written, StringComparison.Ordinal);

    private static bool IsPlainName(string? name)
    {
        // the public arenas (0, 1, ...) share one folder, named after their base name
        if (name == PublicCounts.PublicGroup) return true;
        // a draft (#name) is its own arena with its own folder; '#' only as the first character
        if (!string.IsNullOrEmpty(name) && DraftRules.IsDraft(name)) name = name[1..];
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;
        if (!(char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))) return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return false;
        return true;
    }
}
