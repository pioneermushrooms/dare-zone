using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// Flag:WinDelay as a real hold time (the server reads it but its FlagGamePoints wins at once), and who wins when
// the round's time runs out. Bug: "as soon as i collected all the flags, it instantly awarded me points".
public sealed class FlagHoldTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
    private static FlagView Carried(short f) => new(FlagSpot.Carried, f);
    private static FlagView Dropped(short f) => new(FlagSpot.OnMap, f);
    private static readonly FlagView Neutral = new(FlagSpot.OnMap, -1);

    [Fact]
    public void carry_all_needs_every_flag_carried_by_one_team()
    {
        Assert.Equal((short)1, FlagHold.Holder(FlagWinMode.CarryAll, [Carried(1), Carried(1)]));
        Assert.Null(FlagHold.Holder(FlagWinMode.CarryAll, [Carried(1), Dropped(1)]));
        Assert.Null(FlagHold.Holder(FlagWinMode.CarryAll, [Carried(1), Carried(2)]));
        Assert.Null(FlagHold.Holder(FlagWinMode.CarryAll, []));
    }

    [Fact]
    public void own_all_dropped_needs_every_flag_on_the_map_owned_by_one_team()
    {
        Assert.Equal((short)0, FlagHold.Holder(FlagWinMode.OwnAllDropped, [Dropped(0), Dropped(0)]));
        Assert.Null(FlagHold.Holder(FlagWinMode.OwnAllDropped, [Dropped(0), Carried(0)]));
        Assert.Null(FlagHold.Holder(FlagWinMode.OwnAllDropped, [Dropped(0), Neutral]));
        Assert.Null(FlagHold.Holder(FlagWinMode.None, [Dropped(0)]));
    }

    [Fact]
    public void most_flags_wins_on_time_and_a_tie_is_a_draw()
    {
        Assert.Equal((short)2, FlagHold.MostFlags([Carried(2), Dropped(2), Dropped(1), Neutral]));
        Assert.Null(FlagHold.MostFlags([Carried(2), Dropped(1)]));
        Assert.Null(FlagHold.MostFlags([Neutral, Neutral]));
    }

    [Fact]
    public void mode_parses_names_and_numbers()
    {
        Assert.Equal(FlagWinMode.CarryAll, FlagHold.ParseMode("carryall"));
        Assert.Equal(FlagWinMode.OwnAllDropped, FlagHold.ParseMode("1"));
        Assert.Equal(FlagWinMode.None, FlagHold.ParseMode("-1"));
        Assert.Equal(FlagWinMode.None, FlagHold.ParseMode(null));
        Assert.Equal(FlagWinMode.None, FlagHold.ParseMode("bogus"));
    }

    [Fact]
    public void holding_every_flag_starts_a_countdown_instead_of_winning()
    {
        var c = new FlagHoldClock();
        var e = c.Update(1, T0, TimeSpan.FromMinutes(6));
        Assert.Equal(FlagHoldStep.Started, e.Step);
        Assert.Equal((short)1, e.Freq);
        Assert.Equal(TimeSpan.FromMinutes(6), e.Remaining);
        Assert.Equal(FlagHoldStep.None, c.Update(1, T0.AddSeconds(1), TimeSpan.FromMinutes(6)).Step);
    }

    [Fact]
    public void the_win_comes_when_the_countdown_ends()
    {
        var c = new FlagHoldClock();
        c.Update(1, T0, TimeSpan.FromSeconds(10));
        Assert.NotEqual(FlagHoldStep.Won, c.Update(1, T0.AddSeconds(9), TimeSpan.FromSeconds(10)).Step);
        var e = c.Update(1, T0.AddSeconds(10), TimeSpan.FromSeconds(10));
        Assert.Equal(FlagHoldStep.Won, e.Step);
        Assert.Equal((short)1, e.Freq);
        Assert.Null(c.Freq); // over: the next hold starts afresh
    }

    [Fact]
    public void losing_a_flag_cancels_the_countdown()
    {
        var c = new FlagHoldClock();
        c.Update(1, T0, TimeSpan.FromSeconds(10));
        var e = c.Update(null, T0.AddSeconds(5), TimeSpan.FromSeconds(10));
        Assert.Equal(FlagHoldStep.Cancelled, e.Step);
        Assert.Equal((short)1, e.Freq);
        Assert.Equal(FlagHoldStep.None, c.Update(null, T0.AddSeconds(6), TimeSpan.FromSeconds(10)).Step);
        Assert.Equal(FlagHoldStep.Started, c.Update(1, T0.AddSeconds(7), TimeSpan.FromSeconds(10)).Step); // full time again
        Assert.Equal(FlagHoldStep.Won, c.Update(1, T0.AddSeconds(17), TimeSpan.FromSeconds(10)).Step);
    }

    [Fact]
    public void another_team_taking_every_flag_restarts_the_countdown_for_them()
    {
        var c = new FlagHoldClock();
        c.Update(1, T0, TimeSpan.FromSeconds(10));
        var e = c.Update(2, T0.AddSeconds(8), TimeSpan.FromSeconds(10));
        Assert.Equal(FlagHoldStep.Started, e.Step);
        Assert.Equal((short)2, e.Freq);
        Assert.Equal((short)1, e.Previous);
        Assert.NotEqual(FlagHoldStep.Won, c.Update(2, T0.AddSeconds(12), TimeSpan.FromSeconds(10)).Step);
    }

    [Fact]
    public void no_hold_time_wins_at_once()
    {
        var c = new FlagHoldClock();
        var e = c.Update(3, T0, TimeSpan.Zero);
        Assert.Equal(FlagHoldStep.Won, e.Step);
        Assert.Equal((short)3, e.Freq);
    }

    [Fact]
    public void warnings_at_each_minute_then_30_and_10_seconds()
    {
        var c = new FlagHoldClock();
        c.Update(0, T0, TimeSpan.FromSeconds(150));
        var warned = new List<int>();
        for (var s = 1; s < 150; s++)
        {
            var e = c.Update(0, T0.AddSeconds(s), TimeSpan.FromSeconds(150));
            if (e.Step == FlagHoldStep.Warn) warned.Add((int)e.Remaining.TotalSeconds);
        }
        Assert.Equal([120, 60, 30, 10], warned);
    }

    [Fact]
    public void a_late_tick_warns_once_not_for_every_mark_it_skipped()
    {
        var c = new FlagHoldClock();
        c.Update(0, T0, TimeSpan.FromSeconds(100));
        var e = c.Update(0, T0.AddSeconds(95), TimeSpan.FromSeconds(100));
        Assert.Equal(FlagHoldStep.Warn, e.Step);
        Assert.Equal(FlagHoldStep.None, c.Update(0, T0.AddSeconds(96), TimeSpan.FromSeconds(100)).Step);
    }

    [Fact]
    public void reset_forgets_a_hold()
    {
        var c = new FlagHoldClock();
        c.Update(0, T0, TimeSpan.FromSeconds(10));
        c.Reset();
        Assert.Null(c.Freq);
        Assert.Equal(FlagHoldStep.Started, c.Update(0, T0.AddSeconds(1), TimeSpan.FromSeconds(10)).Step);
    }

    [Fact]
    public void flags_map_to_our_hold_rule_not_the_servers_instant_win()
    {
        Assert.Equal([FlagHold.TypeName], GameModeAttach.TypeNames(GameModeModules.Flags));
        Assert.DoesNotContain("SS.Core.Modules.Scoring.FlagGamePoints", GameModeAttach.TypeNames(GameModeModules.Flags));
    }

    [Fact]
    public void the_time_text_reads_naturally()
    {
        Assert.Equal("6 minutes", FlagHold.Say(TimeSpan.FromMinutes(6)));
        Assert.Equal("1 minute", FlagHold.Say(TimeSpan.FromMinutes(1)));
        Assert.Equal("2:30", FlagHold.Say(TimeSpan.FromSeconds(150)));
        Assert.Equal("30 seconds", FlagHold.Say(TimeSpan.FromSeconds(30)));
        Assert.Equal("1 second", FlagHold.Say(TimeSpan.FromSeconds(1)));
    }
}
