using Dare.Arenas.Core;
using Xunit;

namespace Dare.Arenas.Tests;

public class PublicCountsTests
{
    [Fact]
    public void Counts_players_in_the_public_arenas_only()
    {
        // The server groups its public arenas 0, 1, ... under "(public)"; owned and
        // private arenas have their own base names.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["(public)"] = 3, ["duel"] = 1, ["#secret"] = 2,
        };
        Assert.Equal(3, PublicCounts.PublicPlayers(counts));
    }

    [Fact]
    public void Nobody_in_public_is_zero()
    {
        Assert.Equal(0, PublicCounts.PublicPlayers(new Dictionary<string, int> { ["duel"] = 4 }));
    }
}
