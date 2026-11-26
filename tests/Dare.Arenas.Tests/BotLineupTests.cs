using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class BotLineupTests
{
    [Fact]
    public void renders_the_wire_format()
    {
        var rex = new ArenaBot("Rex", "ship 3\nbehaviour fighter\nteam 1\npriority 9\npoint 10 20\ncasual 3 dry\nbotchat on\nannounce 60 hi all\nreply rules no camping", true);
        var ida = new ArenaBot("Ida Two", "behaviour idle\ngreet Hello {name}", true);
        var gone = new ArenaBot("Gone", "ship 1", true);
        var text = BotLineup.Render("duel", "Owner", [
            (rex, new BotPlacement("Rex", BotSeat.In, 1), "PASS1"),
            (ida, new BotPlacement("Ida Two", BotSeat.Spectating, 0), "PASS2"),
            (gone, new BotPlacement("Gone", BotSeat.Out, 0), "PASS3"),
        ]);
        Assert.Equal(
            "lineup duel\ncontroller Owner\n" +
            "bot Rex\npassword PASS1\nseat in\nteam 1\nship 3\nbehaviour fighter\npoint 10 20\nreply rules no camping\ngreet Oh, hello {name}.\nend\n" +
            "bot Ida Two\npassword PASS2\nseat spec\nteam 0\nship 1\nbehaviour idle\ngreet Hello {name}\nend\n" +
            "done\n", text);
    }

    [Fact]
    public void a_casual_bot_with_its_own_greeting_keeps_it()
    {
        var bot = new ArenaBot("Rex", "ship 2\ngreet Yo {name}\ncasual 1 competitive", true);
        var text = BotLineup.Render("duel", "Owner", [(bot, new BotPlacement("Rex", BotSeat.In, 0), "P")]);
        Assert.Contains("greet Yo {name}\nend\n", text);
        Assert.DoesNotContain("casual", text);
        Assert.DoesNotContain("You're up", text);
    }
}

public class BotStaggerTests
{
    private static BotPlacement P(string label, BotSeat seat, int team = 0) => new(label, seat, team);

    [Fact]
    public void a_lone_change_goes_through_at_once()
    {
        var now = DateTimeOffset.UnixEpoch;
        var stagger = new BotStagger(() => now, new Random(1));
        Assert.Equal([P("A", BotSeat.Out)], stagger.Apply([P("A", BotSeat.Out)]));
        Assert.Equal([P("A", BotSeat.In)], stagger.Apply([P("A", BotSeat.In)]));
    }

    [Fact]
    public void changes_in_the_same_tick_are_spread_over_six_seconds()
    {
        var now = DateTimeOffset.UnixEpoch;
        var stagger = new BotStagger(() => now, new Random(7));
        var all = new[] { "A", "B", "C", "D", "E", "F" };
        stagger.Apply(all.Select(l => P(l, BotSeat.In)).ToList());
        var leaving = all.Select(l => P(l, BotSeat.Spectating)).ToList();
        var when = new Dictionary<string, int>();
        for (int s = 0; s <= 7; s++)
        {
            now = DateTimeOffset.UnixEpoch.AddSeconds(s);
            foreach (var p in stagger.Apply(leaving))
                if (p.Seat == BotSeat.Spectating) when.TryAdd(p.Label, s);
        }
        Assert.Equal(all.Length, when.Count);           // everyone got there within 6 s
        Assert.True(when.Values.Max() <= 6);
        Assert.True(when.Values.Distinct().Count() > 1); // not all in the same second
    }

    [Fact]
    public void a_change_taken_back_before_it_shows_is_dropped()
    {
        var now = DateTimeOffset.UnixEpoch;
        var stagger = new BotStagger(() => now, new Random(3));
        stagger.Apply([P("A", BotSeat.In), P("B", BotSeat.In)]);
        stagger.Apply([P("A", BotSeat.Out), P("B", BotSeat.Out)]);
        var back = stagger.Apply([P("A", BotSeat.In), P("B", BotSeat.In)]);
        Assert.Equal([P("A", BotSeat.In), P("B", BotSeat.In)], back);
        now = now.AddSeconds(10);
        Assert.Equal([P("A", BotSeat.In), P("B", BotSeat.In)], stagger.Apply([P("A", BotSeat.In), P("B", BotSeat.In)]));
    }

    [Fact]
    public void a_new_bot_out_needs_no_wait_and_a_gone_bot_is_forgotten()
    {
        var now = DateTimeOffset.UnixEpoch;
        var stagger = new BotStagger(() => now, new Random(3));
        Assert.Equal([P("A", BotSeat.Out), P("B", BotSeat.Out)], stagger.Apply([P("A", BotSeat.Out), P("B", BotSeat.Out)]));
        Assert.Equal([P("B", BotSeat.Out)], stagger.Apply([P("B", BotSeat.Out)]));
    }
}
