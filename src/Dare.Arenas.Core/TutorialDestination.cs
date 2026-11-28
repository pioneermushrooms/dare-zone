namespace Dare.Arenas.Core;

/// <summary>Where a player goes when their tutorial ends.</summary>
public static class TutorialDestination
{
    /// <summary>The zone's main public arena: the server's public arena 0 (the "public" of the arena lists, Continuum's default).</summary>
    public const string MainPublicArena = "0";

    /// <summary>
    /// A replay goes back to the arena it was started from. Otherwise the listed arena (not <c>#</c> private, not a
    /// tutorial arena) with the most real pilots; ties go to the public arenas (0, 1, ...) first, then by name. With
    /// nobody on anywhere, the main public arena, and <c>NobodyOn</c> so the player is told where the bots fly.
    /// </summary>
    /// <param name="arenas">Each arena the caller would list, with its real pilots (no bots, no fake players).</param>
    public static (string Arena, bool NobodyOn) Pick(IReadOnlyList<(string Arena, int RealPilots)> arenas, string? origin, bool replay)
    {
        if (replay && !string.IsNullOrEmpty(origin) && !TutorialCast.IsTutorialArena(origin)) return (origin, false);
        var best = arenas
            .Where(a => a.RealPilots > 0 && a.Arena.Length > 0 && a.Arena[0] != '#')
            .OrderByDescending(a => a.RealPilots)
            .ThenBy(a => PublicNumber(a.Arena))
            .ThenBy(a => a.Arena, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Arena)
            .FirstOrDefault();
        return best is null ? (MainPublicArena, true) : (best, false);
    }

    // Public arenas are named by number alone; every other arena sorts after them.
    private static long PublicNumber(string arena) =>
        arena.All(char.IsAsciiDigit) && long.TryParse(arena, out var n) ? n : long.MaxValue;
}
