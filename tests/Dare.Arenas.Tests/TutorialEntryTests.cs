using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class TutorialEntryTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static TutorialRecord Rec(string? outcome = null, int? act = null, int dropouts = 0) =>
        new(T, outcome, act is null ? null : 3, act, dropouts);

    private static (TutorialStart, int) D(EntryKind k, TutorialRecord? r = null, bool done = false,
        bool pub = true, bool first = true, bool room = true) =>
        TutorialEntry.Decide(k, r, done, pub, first, room);

    [Fact] public void Bot_No() => Assert.Equal((TutorialStart.No, 0), D(EntryKind.Bot));
    [Fact] public void Fake_No() => Assert.Equal((TutorialStart.No, 0), D(EntryKind.Fake));
    [Fact] public void Guest_Done_No() => Assert.Equal((TutorialStart.No, 0), D(EntryKind.Guest, done: true));
    [Fact] public void Guest_Public_Start() => Assert.Equal((TutorialStart.Start, 0), D(EntryKind.Guest));
    [Fact] public void Guest_OtherArena_Offer() => Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Guest, pub: false));
    [Fact] public void Account_NoRow_Public_Start() => Assert.Equal((TutorialStart.Start, 0), D(EntryKind.Account));
    [Fact] public void Account_NoRow_OtherArena_Offer() => Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Account, pub: false));

    [Theory]
    [InlineData("existing")]
    [InlineData("finished")]
    [InlineData("left")]
    public void Account_WithOutcome_No(string outcome) =>
        Assert.Equal((TutorialStart.No, 0), D(EntryKind.Account, Rec(outcome)));

    [Fact] public void Account_InProgress_OneDropout_ResumesAtAct() =>
        Assert.Equal((TutorialStart.Resume, 2), D(EntryKind.Account, Rec(act: 2, dropouts: 1)));
    [Fact] public void Account_InProgress_NoActYet_ResumesAtZero() =>
        Assert.Equal((TutorialStart.Resume, 0), D(EntryKind.Account, Rec()));
    [Fact] public void Account_InProgress_TwoDropouts_No() =>
        Assert.Equal((TutorialStart.No, 0), D(EntryKind.Account, Rec(act: 2, dropouts: 2)));
    [Fact] public void Resume_OtherArena_Offer() =>
        Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Account, Rec(act: 2, dropouts: 1), pub: false));
    [Fact] public void Resume_NoCapacity_Offer() =>
        Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Account, Rec(act: 2, dropouts: 1), room: false));
    [Fact] public void NotFirstEntry_No() => Assert.Equal((TutorialStart.No, 0), D(EntryKind.Account, first: false));
    [Fact] public void NoCapacity_Offer() => Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Account, room: false));
}

public class TutorialActNameTests
{
    [Theory]
    [InlineData("controls", 1)]
    [InlineData("SWARM", 2)]
    [InlineData(" objectives ", 3)]
    public void ActNamed_Known(string name, int act) => Assert.Equal(act, TutorialEntry.ActNamed(name));

    [Theory][InlineData("")][InlineData("1")][InlineData("flags")]
    public void ActNamed_Unknown_Null(string name) => Assert.Null(TutorialEntry.ActNamed(name));
}
