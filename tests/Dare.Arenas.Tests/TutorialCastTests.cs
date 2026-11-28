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

public class TutorialWholeCastTests
{
    private static CastLine L(string role, string seat = "in", string script = "ship 2") => new(role, seat, script);

    private static readonly IReadOnlyList<CastLine>[] Steps =
    [
        [],
        [L("target", script: "ship 2\npoint 1 1")],
        [L("swarm"), L("swarm"), L("swarm")],
        [L("teammate", script: "ship 1")],
    ];

    [Fact] public void WholeCast_OneBotPerRoleSlot_AllSpectating()
    {
        var whole = TutorialCast.WholeCast(Steps, shareRoles: false);
        Assert.Equal(["target", "swarm", "swarm", "swarm", "teammate"], whole.Select(c => c.Role));
        Assert.All(whole, c => Assert.Equal("spec", c.Seat));
        Assert.Equal("ship 2\npoint 1 1", whole[0].Script);
        Assert.Equal("ship 1", whole[4].Script);
    }

    [Fact] public void WholeCast_Lean_SharesOneBotAcrossRoles()
    {
        IReadOnlyList<CastLine>[] lean = [[L("target")], [L("swarm")], [L("teammate", script: "ship 1")]];
        var whole = TutorialCast.WholeCast(lean, shareRoles: true);
        Assert.Single(whole);
        Assert.Equal("target", whole[0].Role);
        Assert.Equal("spec", whole[0].Seat);
    }

    [Fact] public void ForStep_SeatsTheStepsRoles_OthersSpectate()
    {
        var whole = TutorialCast.WholeCast(Steps, false);
        var now = TutorialCast.ForStep(whole, Steps[2], false);
        Assert.Equal(["target", "swarm", "swarm", "swarm", "teammate"], now.Select(c => c.Role));
        Assert.Equal(["spec", "in", "in", "in", "spec"], now.Select(c => c.Seat));
    }

    [Fact] public void ForStep_EmptyStep_EveryoneSpectates()
    {
        var whole = TutorialCast.WholeCast(Steps, false);
        Assert.All(TutorialCast.ForStep(whole, [], false), c => Assert.Equal("spec", c.Seat));
    }

    [Fact] public void ForStep_Lean_TheBotTakesTheStepsRoleAndScript()
    {
        IReadOnlyList<CastLine>[] lean = [[L("target")], [L("teammate", script: "ship 1")]];
        var whole = TutorialCast.WholeCast(lean, true);
        var now = TutorialCast.ForStep(whole, lean[1], true);
        Assert.Equal(new CastLine("teammate", "in", "ship 1"), Assert.Single(now));
    }

    [Fact] public void ForStep_LineupsKeepTheSameNamesAcrossSteps()
    {
        var whole = TutorialCast.WholeCast(Steps, false);
        string[] names = ["A", "B", "C", "D", "E"];
        var a = TutorialCast.Lineup("#training1", TutorialCast.ForStep(whole, Steps[1], false), names);
        var b = TutorialCast.Lineup("#training1", TutorialCast.ForStep(whole, Steps[3], false), names);
        Assert.Equal(a.Select(x => x.Bot.Label), b.Select(x => x.Bot.Label));
        Assert.Equal(BotSeat.In, a[0].Placement.Seat);
        Assert.Equal(BotSeat.Spectating, b[0].Placement.Seat);
        Assert.Equal((BotSeat.In, 0), (b[4].Placement.Seat, b[4].Placement.Team));
    }
}

public class TutorialLineupTextTests
{
    // oss-bot greets publicly ("I take flight orders from the arena owner...") whenever its lineup names a controller,
    // and that controller may order the bots about: the tutorial's bots must have none.
    [Fact] public void RenderLineup_HasNoController()
    {
        var lineup = TutorialCast.Lineup("#training1", [new CastLine("swarm", "in", "ship 2\nbehaviour swarm\nrange 12")], ["Wobbly Nebula"]);
        var text = TutorialCast.RenderLineup("#training1", lineup.Select(l => (l.Bot, l.Placement, "PASS")).ToList());
        Assert.StartsWith("lineup #training1\nbot Wobbly Nebula\n", text);
        Assert.DoesNotContain("controller", text);
        Assert.EndsWith("done\n", text);
    }

    [Fact] public void Render_NullController_OmitsTheLine() =>
        Assert.Equal("lineup duel\ndone\n", BotLineup.Render("duel", null, []));

    [Fact] public void Render_Controller_StillNamed() =>
        Assert.Equal("lineup duel\ncontroller Chris\ndone\n", BotLineup.Render("duel", "Chris", []));
}
