namespace Dare.Arenas.Core;

/// <summary>One player as the bot supervisor sees them in a tick.</summary>
/// <param name="BotSession">Logged in on a bot's one-time pass. This, not the name, is what makes a player a bot:
/// a pilot account holding a bot's name is a pilot.</param>
/// <param name="Fake">A server-side fake player (autoturret and the like): not a person.</param>
public sealed record CensusEntry(string Name, string? Arena, bool InBaseArena, bool Playing, bool InShip, int Freq,
    bool BotSession, bool Fake);

/// <param name="Occupied">Arenas with a real person in them (base arenas only).</param>
/// <param name="Flying">Real people in a ship, per arena and freq.</param>
/// <param name="BotsInZone">Names of the bot sessions logged in to the zone (any arena or none).</param>
public sealed record Census(IReadOnlySet<string> Occupied,
    IReadOnlyDictionary<string, Dictionary<int, int>> Flying, IReadOnlySet<string> BotsInZone);

public static class ZoneCensus
{
    /// <summary>A person: not a bot session and not a fake player.</summary>
    public static bool IsPerson(CensusEntry p) => !p.BotSession && !p.Fake;

    public static Census Take(IEnumerable<CensusEntry> players)
    {
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flying = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        var bots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in players)
        {
            if (p.BotSession)
            {
                bots.Add(p.Name);
                continue;
            }
            if (p.Fake || !p.Playing || p.Arena is not { } arena || !p.InBaseArena) continue;
            occupied.Add(arena);
            if (!p.InShip) continue;
            if (!flying.TryGetValue(arena, out var freqs)) flying[arena] = freqs = new();
            freqs[p.Freq] = freqs.GetValueOrDefault(p.Freq) + 1;
        }
        return new(occupied, flying, bots);
    }
}
