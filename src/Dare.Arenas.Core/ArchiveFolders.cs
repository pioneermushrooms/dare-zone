using System.Globalization;

namespace Dare.Arenas.Core;

/// <summary>
/// Folder names under <c>arenas/_archive/</c>. An arena is archived as <c>&lt;name&gt;</c>, or
/// <c>&lt;name&gt;-2</c>, <c>-3</c>, ... when earlier archives exist, so nothing is overwritten.
/// <paramref name="arena"/> must be a validated arena name (it is used as a folder name).
/// </summary>
public static class ArchiveFolders
{
    /// <summary>The folder name for a new archive of <paramref name="arena"/>, given the names already there.</summary>
    public static string NextName(IEnumerable<string> existing, string arena)
    {
        var taken = Suffixes(existing, arena).ToHashSet();
        if (!taken.Contains(1)) return arena;
        var n = 2;
        while (taken.Contains(n)) n++;
        return $"{arena}-{n}";
    }

    /// <summary>The most recent archive of <paramref name="arena"/> (highest suffix), or null.</summary>
    public static string? Latest(IEnumerable<string> existing, string arena)
    {
        var max = Suffixes(existing, arena).DefaultIfEmpty(0).Max();
        return max switch
        {
            0 => null,
            1 => arena,
            _ => $"{arena}-{max}",
        };
    }

    // 1 for the plain name, n for "<arena>-n" (n >= 2); other names are ignored.
    private static IEnumerable<int> Suffixes(IEnumerable<string> existing, string arena)
    {
        foreach (var e in existing)
        {
            if (string.Equals(e, arena, StringComparison.OrdinalIgnoreCase))
            {
                yield return 1;
                continue;
            }
            if (e.Length > arena.Length + 1
                && e.StartsWith(arena + "-", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(e.AsSpan(arena.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && n >= 2)
                yield return n;
        }
    }
}
