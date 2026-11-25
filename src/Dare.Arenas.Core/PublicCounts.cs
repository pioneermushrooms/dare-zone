namespace Dare.Arenas.Core;

/// <summary>Pilot counts for the public feed.</summary>
public static class PublicCounts
{
    /// <summary>The base name the server gives its public arenas 0, 1, ... (SS.Core Constants.ArenaGroup_Public).</summary>
    public const string PublicGroup = "(public)";

    /// <summary>Players in the public arenas, from per-base-name counts.</summary>
    public static int PublicPlayers(IReadOnlyDictionary<string, int> counts) =>
        counts.TryGetValue(PublicGroup, out var n) ? n : 0;
}
