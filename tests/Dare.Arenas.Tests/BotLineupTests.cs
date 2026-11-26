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
    public void every_botchat_line_is_zone_only()
    {
        Assert.Equal(["ship 1", "behaviour idle"], BotLineup.ScriptLines("behaviour idle\nbotchat off\nbotchat  on"));
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

public class BotLineupHostLimitTests
{
    // What oss-bot's lineup.rs and script.rs accept; a block outside these makes the host refuse the whole lineup.
    private static readonly HashSet<string> HostKeys = ["ship", "behaviour", "point", "range", "greet", "reply"];

    private static void AssertHostTakes(string block)
    {
        var lines = block.Split('\n');
        Assert.Matches("^bot [A-Za-z0-9][A-Za-z0-9 _.-]{0,19}$", lines[0]);
        Assert.DoesNotContain("  ", lines[0]);
        int points = 0, replies = 0;
        foreach (var line in lines.Skip(1).TakeWhile(l => l != "end"))
        {
            Assert.All(line, c => Assert.InRange(c, ' ', '~'));
            var (key, value) = (line.Split(' ', 2)[0], line.Split(' ', 2).ElementAtOrDefault(1) ?? "");
            switch (key)
            {
                case "password": Assert.NotEmpty(value); break;
                case "seat": Assert.Contains(value, new[] { "in", "spec" }); break;
                case "team": Assert.InRange(int.Parse(value), 0, 9999); break;
                case "ship": Assert.InRange(int.Parse(value), 1, 8); break;
                case "behaviour": Assert.Contains(value, new[] { "idle", "patrol", "sentry", "fighter" }); break;
                case "point":
                    Assert.True(++points <= 64);
                    Assert.All(value.Split(' '), v => Assert.InRange(int.Parse(v), 0, 1023)); break;
                case "range": Assert.InRange(int.Parse(value), 1, 200); break;
                case "greet": Assert.InRange(value.Length, 0, 200); break;
                case "reply":
                    Assert.True(++replies <= 20);
                    Assert.InRange(value.Split(' ')[0].Length, 1, BotLineup.MaxReplyKeyword); break;
                default: Assert.Fail($"the host does not know {key}"); break;
            }
            Assert.True(key is "password" or "seat" or "team" || HostKeys.Contains(key));
        }
    }

    /// <summary>Every line kind the zone accepts, each at its limit (ArenaStoreTests saves it).</summary>
    internal static readonly string MaxScript = string.Join("\n", new[]
        {
            "ship 8", "behaviour sentry", "team 9999", "priority 9", "range 200", "botchat on", "casual 5 competitive",
            "greet " + new string('g', 200), "announce 86400 " + new string('a', 100),
        }.Concat(Enumerable.Range(0, 64).Select(i => $"point {i * 16} 1023"))
         .Concat(Enumerable.Range(0, 20).Select(i => $"reply {new string((char)('a' + i), 24)} {new string('r', 60)}")));

    [Fact]
    public void every_whitelisted_line_kind_renders_within_the_host_limits()
    {
        var script = MaxScript;
        Assert.True(script.Length <= 3900, $"script is {script.Length} long");
        var bot = new ArenaBot("Max_Bot-1.2 ok", script, true);
        Assert.Null(BotLineup.HostProblem(bot));
        foreach (var kinds in new[] { "behaviour idle", "behaviour patrol\npoint 0 0", "behaviour fighter\nteam 0", "behaviour idle\ncasual 1 friendly", "behaviour idle\ncasual 2 dry\nbotchat on" })
        {
            var b = new ArenaBot("B", kinds, true);
            Assert.Null(BotLineup.HostProblem(b));
            var text = BotLineup.Render("a", "o", [(b, new BotPlacement("B", BotSeat.In, 0), "P")]);
            AssertHostTakes(text.Split("controller o\n")[1]);
        }
        var full = BotLineup.Render("a", "o", [(bot, new BotPlacement(bot.Label, BotSeat.Spectating, 9999), "P")]);
        AssertHostTakes(full.Split("controller o\n")[1]);
    }

    [Fact]
    public void a_bot_the_host_would_refuse_is_left_out_of_the_lineup()
    {
        var bad = new ArenaBot("Chatty", "behaviour idle\nreply " + new string('k', 25) + " hi", true);
        var badName = new ArenaBot("_x", "behaviour idle", true);
        var odd = new ArenaBot("Odd", "behaviour idle\nwarp 1", true);
        var good = new ArenaBot("Rex", "behaviour idle", true);
        Assert.Equal("a reply trigger is longer than 24 characters", BotLineup.HostProblem(bad));
        Assert.Equal("the bot host cannot use this name", BotLineup.HostProblem(badName));
        Assert.Equal("the bot host does not know the setting \"warp\"", BotLineup.HostProblem(odd));
        var text = BotLineup.Render("a", "o", [
            (bad, new BotPlacement("Chatty", BotSeat.In, 0), "P1"),
            (badName, new BotPlacement("_x", BotSeat.In, 0), "P2"),
            (odd, new BotPlacement("Odd", BotSeat.In, 0), "P3"),
            (good, new BotPlacement("Rex", BotSeat.In, 0), "P4"),
        ]);
        Assert.Equal("lineup a\ncontroller o\nbot Rex\npassword P4\nseat in\nteam 0\nship 1\nbehaviour idle\nend\ndone\n", text);
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

public class ZoneCensusTests
{
    private static CensusEntry Pilot(string name, int freq = 0, bool ship = true, bool bot = false, bool fake = false) =>
        new(name, "duel", true, true, ship, freq, bot, fake);

    [Fact]
    public void a_pilot_holding_a_bots_name_is_a_pilot()
    {
        // "Rex" is in the bot register, but this Rex logged in with an account, not a bot pass
        var rex = Pilot("Rex", freq: 1);
        var census = ZoneCensus.Take([rex]);
        Assert.True(ZoneCensus.IsPerson(rex));
        Assert.Contains("duel", census.Occupied);
        Assert.Equal(1, census.Flying["duel"][1]);
        Assert.Empty(census.BotsInZone);
    }

    [Fact]
    public void bot_sessions_and_fake_players_are_not_people()
    {
        var census = ZoneCensus.Take([Pilot("Rex", bot: true), Pilot("Turret", fake: true)]);
        Assert.Empty(census.Occupied);
        Assert.Empty(census.Flying);
        Assert.Equal(["Rex"], census.BotsInZone);
        Assert.False(ZoneCensus.IsPerson(Pilot("Rex", bot: true)));
        Assert.False(ZoneCensus.IsPerson(Pilot("Turret", fake: true)));
    }

    [Fact]
    public void spectators_occupy_but_do_not_fly_and_sub_arenas_do_not_count()
    {
        var census = ZoneCensus.Take([Pilot("Ann", ship: false), new("Bob", "duel1", false, true, true, 0, false, false)]);
        Assert.Equal(["duel"], census.Occupied);
        Assert.Empty(census.Flying);
    }
}
