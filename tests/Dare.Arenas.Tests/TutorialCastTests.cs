using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class TutorialCastTests
{
    [Fact] public void Allocate_GivesLowestFree() =>
        Assert.Equal("#training2", TutorialCast.Allocate(new HashSet<string> { "#training1", "#training3" }));

    [Fact] public void Allocate_WhenNoneInUse_GivesFirst() =>
        Assert.Equal("#training1", TutorialCast.Allocate(new HashSet<string>()));

    [Fact] public void Allocate_GivesDistinctArenas()
    {
        var inUse = new HashSet<string>();
        var first = TutorialCast.Allocate(inUse);
        inUse.Add(first);
        Assert.NotEqual(first, TutorialCast.Allocate(inUse));
    }

    [Theory]
    [InlineData("#training1", true)]
    [InlineData("#training12", true)]
    [InlineData("#TRAINING3", true)]
    [InlineData("#training", false)]
    [InlineData("#training1a", false)]
    [InlineData("#trainingx", false)]
    [InlineData("#public", false)]
    [InlineData("", false)]
    public void IsTutorialArena_OnlyNumberedTrainingArenas(string name, bool expected) =>
        Assert.Equal(expected, TutorialCast.IsTutorialArena(name));

    [Theory][InlineData(9, 10, false)][InlineData(10, 10, true)][InlineData(11, 10, true)]
    public void Lean_BeyondFullCasts(int r, int f, bool lean) => Assert.Equal(lean, TutorialCast.Lean(r, f));

    [Theory][InlineData(0, 1, true)][InlineData(1, 1, false)][InlineData(2, 1, false)]
    public void HasCapacity_BelowMax(int running, int max, bool ok) => Assert.Equal(ok, TutorialCast.HasCapacity(running, max));

    [Fact] public void Lineup_TeamsSeatsAndLabels()
    {
        var cast = new[]
        {
            new CastLine("target", "in", "ship 1"),
            new CastLine("swarm", "in", "ship 2"),
            new CastLine("teammate", "spec", "ship 3"),
        };
        var lineup = TutorialCast.Lineup("#training1", cast, ["A", "B", "C", "spare"]);
        Assert.Equal(3, lineup.Count);
        Assert.Equal(["A", "B", "C"], lineup.Select(l => l.Bot.Label));
        Assert.Equal(["ship 1", "ship 2", "ship 3"], lineup.Select(l => l.Bot.Script));
        Assert.All(lineup, l => Assert.True(l.Bot.Deployed));
        Assert.Equal([1, 1, 0], lineup.Select(l => l.Placement.Team));
        Assert.Equal([BotSeat.In, BotSeat.In, BotSeat.Spectating], lineup.Select(l => l.Placement.Seat));
        Assert.Equal(["A", "B", "C"], lineup.Select(l => l.Placement.Label));
    }

    [Fact] public void Lineup_EmptyCastIsValid() => Assert.Empty(TutorialCast.Lineup("#training1", [], []));

    [Fact] public void Lineup_TooFewNames_Throws() =>
        Assert.Throws<ArgumentException>(() => TutorialCast.Lineup("#training1", [new CastLine("swarm", "in", "ship 1")], []));

    [Fact] public void PickNames_AreDistinctSkipTakenAndValid()
    {
        var rng = new Random(7);
        var taken = new HashSet<string> { "Wobbly Nebula" };
        var names = TutorialCast.PickNames(8, rng.Next, taken.Contains);
        Assert.Equal(8, names.Count);
        Assert.Equal(8, names.Distinct().Count());
        Assert.DoesNotContain("Wobbly Nebula", names);
        Assert.All(names, n => Assert.Null(BotLineup.HostProblem(new ArenaBot(n, "ship 1", true))));
    }

    [Fact] public void PickNames_WhenNothingFree_ReturnsWhatItFound() =>
        Assert.Empty(TutorialCast.PickNames(3, new Random(1).Next, _ => true));

    [Fact] public void PickNames_ZeroCount_IsEmpty() =>
        Assert.Empty(TutorialCast.PickNames(0, new Random(1).Next, _ => false));
}
