using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// The hint lines are a wire format the Dare client parses (Task 8): these strings are binding.
public class TutorialHintTests
{
    [Fact] public void Say_KeepsPlaceholders() =>
        Assert.Equal("~tut~ say 5/20 {gun} fires your guns.", TutorialHint.Say(5, 20, "{gun} fires your guns."));

    [Theory]
    [InlineData(HudPart.Radar, "~tut~ reveal radar")]
    [InlineData(HudPart.Energy, "~tut~ reveal energy")]
    [InlineData(HudPart.StatBox, "~tut~ reveal statbox")]
    [InlineData(HudPart.Items, "~tut~ reveal items")]
    [InlineData(HudPart.Chat, "~tut~ reveal chat")]
    public void Reveal_LowercasePartName(HudPart part, string line) => Assert.Equal(line, TutorialHint.Reveal(part));

    [Fact] public void Prompts()
    {
        Assert.Equal("~tut~ prompt skip", TutorialHint.PromptSkip());
        Assert.Equal("~tut~ prompt try", TutorialHint.PromptTry());
    }

    [Fact] public void End()
    {
        Assert.Equal("~tut~ end list", TutorialHint.End(showArenaList: true));
        Assert.Equal("~tut~ end none", TutorialHint.End(showArenaList: false));
    }

    [Fact] public void Prefix_StartsEveryLine() =>
        Assert.All(new[] { TutorialHint.Say(1, 1, "x"), TutorialHint.Reveal(HudPart.Chat), TutorialHint.PromptTry(), TutorialHint.End(false) },
            l => Assert.StartsWith(TutorialHint.Prefix, l));
}

public class TutorialLvzTests
{
    [Theory]
    [InlineData(HudPart.Radar, new short[] { 4000 })]
    [InlineData(HudPart.Energy, new short[] { 4001 })]
    [InlineData(HudPart.StatBox, new short[] { 4002 })]
    [InlineData(HudPart.Items, new short[] { 4003 })]
    [InlineData(HudPart.Chat, new short[] { 4004, 4005 })]
    public void CoversOf(HudPart part, short[] ids) => Assert.Equal(ids, TutorialLvz.CoversOf(part));

    [Fact] public void AllCovers_4000To4005() => Assert.Equal(new short[] { 4000, 4001, 4002, 4003, 4004, 4005 }, TutorialLvz.AllCovers);

    [Fact] public void Ids()
    {
        Assert.Equal(4100, TutorialLvz.MentorBox);
        Assert.Equal(4101, TutorialLvz.Portrait);
        Assert.Equal(4102, TutorialLvz.PortraitTalking);
        Assert.Equal(4110, TutorialLvz.SkipPopup);
        Assert.Equal(4111, TutorialLvz.TryPopup);
        Assert.Equal(4200, TutorialLvz.Beacon);
        Assert.Equal(5007, TutorialLvz.Line(new TutorialLine(7, "x")));
    }
}

public class TutorialDestinationTests
{
    [Fact] public void Replay_GoesBackToOrigin() =>
        Assert.Equal(("duel", false), TutorialDestination.Pick([("0", 9)], "duel", replay: true));

    [Fact] public void Replay_WithoutOrigin_PicksLikeANewcomer() =>
        Assert.Equal(("0", false), TutorialDestination.Pick([("0", 9)], null, replay: true));

    [Fact] public void Newcomer_MostRealPilots() =>
        Assert.Equal(("duel", false), TutorialDestination.Pick([("0", 2), ("duel", 5), ("big", 3)], "0", replay: false));

    [Fact] public void Newcomer_IgnoresOrigin() =>
        Assert.Equal(("big", false), TutorialDestination.Pick([("big", 3)], "duel", replay: false));

    [Fact] public void Tie_PublicFirst_ThenName()
    {
        Assert.Equal(("1", false), TutorialDestination.Pick([("alpha", 4), ("1", 4), ("2", 4)], null, false));
        Assert.Equal(("alpha", false), TutorialDestination.Pick([("beta", 4), ("alpha", 4)], null, false));
        Assert.Equal(("2", false), TutorialDestination.Pick([("10", 4), ("2", 4)], null, false));
    }

    [Fact] public void PrivateAndTutorialArenas_AreNotDestinations() =>
        Assert.Equal(("0", true), TutorialDestination.Pick([("#secret", 9), ("#training1", 1), ("#training", 1)], null, false));

    [Fact] public void NobodyOn_MainPublicArena() =>
        Assert.Equal((TutorialDestination.MainPublicArena, true), TutorialDestination.Pick([("0", 0), ("duel", 0)], null, false));

    [Fact] public void MainPublicArena_IsPublicZero() => Assert.Equal("0", TutorialDestination.MainPublicArena);
}
