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
    // a row with no step and no outcome: the account was offered the tutorial once (it entered another arena first)
    // and chose not to play it; it is not new any more
    [Fact] public void Account_OfferedOnce_NoStep_No() =>
        Assert.Equal((TutorialStart.No, 0), D(EntryKind.Account, Rec()));
    [Fact] public void Account_OfferedOnce_NoStep_OtherArena_No() =>
        Assert.Equal((TutorialStart.No, 0), D(EntryKind.Account, Rec(), pub: false));
    [Fact] public void Account_InProgress_StepButNoAct_ResumesAtZero() =>
        Assert.Equal((TutorialStart.Resume, 0), D(EntryKind.Account, new TutorialRecord(T, null, 2, null, 0)));
    [Fact] public void Account_InProgress_TwoDropouts_No() =>
        Assert.Equal((TutorialStart.No, 0), D(EntryKind.Account, Rec(act: 2, dropouts: 2)));
    // a run under way that dropped out is offered (not resumed) when the account enters another arena first
    [Fact] public void Resume_OtherArena_Offer() =>
        Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Account, Rec(act: 2, dropouts: 1), pub: false));
    [Fact] public void Resume_NoCapacity_Offer() =>
        Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Account, Rec(act: 2, dropouts: 1), room: false));
    [Fact] public void NotFirstEntry_No() => Assert.Equal((TutorialStart.No, 0), D(EntryKind.Account, first: false));
    [Fact] public void NoCapacity_Offer() => Assert.Equal((TutorialStart.Offer, 0), D(EntryKind.Account, room: false));

    // the first arena entry of a game login is noted for accounts only, and only from a game client: a chat-protocol
    // client (or the web pages, which never enter an arena) is not a game login
    [Fact] public void GameLogin_Account_StandardClient_FirstEntry() => Assert.True(TutorialEntry.IsGameLogin(EntryKind.Account, true, true));
    [Fact] public void GameLogin_ChatClient_Not() => Assert.False(TutorialEntry.IsGameLogin(EntryKind.Account, false, true));
    [Fact] public void GameLogin_LaterEntry_Not() => Assert.False(TutorialEntry.IsGameLogin(EntryKind.Account, true, false));
    [Theory]
    [InlineData(EntryKind.Guest)]
    [InlineData(EntryKind.Bot)]
    [InlineData(EntryKind.Fake)]
    [InlineData(null)]
    public void GameLogin_NotAnAccount_Not(EntryKind? kind) => Assert.False(TutorialEntry.IsGameLogin(kind, true, true));

    // a training arena is for the player whose run it is (and the run's bots); staff may look in
    [Fact] public void Training_OwnRun_Stays() => Assert.True(TutorialEntry.MayStayInTraining(EntryKind.Account, ownRun: true, staff: false));
    [Fact] public void Training_Stranger_SentAway() => Assert.False(TutorialEntry.MayStayInTraining(EntryKind.Account, ownRun: false, staff: false));
    [Fact] public void Training_Guest_Stranger_SentAway() => Assert.False(TutorialEntry.MayStayInTraining(EntryKind.Guest, ownRun: false, staff: false));
    [Fact] public void Training_NotFoundation_SentAway() => Assert.False(TutorialEntry.MayStayInTraining(null, ownRun: false, staff: false));
    [Fact] public void Training_Staff_Stays() => Assert.True(TutorialEntry.MayStayInTraining(EntryKind.Account, ownRun: false, staff: true));
    [Theory]
    [InlineData(EntryKind.Bot)]
    [InlineData(EntryKind.Fake)]
    public void Training_BotsAndFakes_Stay(EntryKind kind) => Assert.True(TutorialEntry.MayStayInTraining(kind, ownRun: false, staff: false));
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
