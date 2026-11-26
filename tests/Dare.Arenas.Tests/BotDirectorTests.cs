using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class BotDirectorTests
{
    private static ArenaBot Bot(string label, int team = 0, int priority = 5, bool deployed = true) =>
        new(label, $"ship 1\nbehaviour fighter\nteam {team}\npriority {priority}", deployed);

    private static Dictionary<int, int> Map(params (int, int)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static BotPlacement Of(IReadOnlyList<BotPlacement> plan, string label) => plan.Single(p => p.Label == label);

    [Fact]
    public void bots_fill_the_places_players_have_not_taken()
    {
        var bots = new[] { Bot("A", priority: 9), Bot("B", priority: 7), Bot("C", priority: 5), Bot("D", priority: 1) };
        var plan = BotDirector.Plan(bots, Map((0, 4)), Map((0, 1)));
        Assert.Equal(3, plan.Count(p => p.Seat == BotSeat.In));
        Assert.Equal(BotSeat.Out, Of(plan, "D").Seat);
    }

    [Fact]
    public void the_lowest_priority_makes_room_first()
    {
        var bots = new[] { Bot("A", priority: 9), Bot("B", priority: 5), Bot("C", priority: 5), Bot("D", priority: 1) };
        var plan = BotDirector.Plan(bots, Map((0, 4)), Map((0, 1)));
        Assert.Equal(BotSeat.Out, Of(plan, "D").Seat);
        // a tie goes by label: with two places taken, B (earlier label) stays and C leaves
        plan = BotDirector.Plan(bots, Map((0, 4)), Map((0, 2)));
        Assert.Equal(BotSeat.In, Of(plan, "B").Seat);
        Assert.Equal(BotSeat.Out, Of(plan, "C").Seat);
    }

    [Fact]
    public void a_short_side_gets_a_spare_bot()
    {
        var bots = new[] { Bot("Lone", 0), Bot("T1a", 1, 9), Bot("T1b", 1, 7), Bot("T1c", 1, 3) };
        var plan = BotDirector.Plan(bots, Map((0, 2), (1, 2)), Map());
        Assert.Equal(new BotPlacement("T1a", BotSeat.In, 1), Of(plan, "T1a"));
        Assert.Equal(new BotPlacement("T1b", BotSeat.In, 1), Of(plan, "T1b"));
        Assert.Equal(new BotPlacement("T1c", BotSeat.In, 0), Of(plan, "T1c"));
        Assert.Equal(new BotPlacement("Lone", BotSeat.In, 0), Of(plan, "Lone"));
    }

    [Fact]
    public void with_no_spare_the_short_side_stays_short()
    {
        var bots = new[] { Bot("A0", 0), Bot("B0", 0), Bot("A1", 1), Bot("B1", 1) };
        var plan = BotDirector.Plan(bots, Map((0, 2), (1, 2)), Map((1, 2)));
        Assert.Equal(BotSeat.Out, Of(plan, "A1").Seat);
        Assert.Equal(BotSeat.Out, Of(plan, "B1").Seat);
        Assert.Equal(new BotPlacement("A0", BotSeat.In, 0), Of(plan, "A0"));
        Assert.Equal(new BotPlacement("B0", BotSeat.In, 0), Of(plan, "B0"));
    }

    [Fact]
    public void teams_without_a_match_size_are_left_alone()
    {
        var bots = new[] { Bot("X", 5), Bot("Y", 5), Bot("Z", 0) };
        var plan = BotDirector.Plan(bots, Map((0, 1)), Map((5, 8), (0, 1)));
        Assert.Equal(new BotPlacement("X", BotSeat.In, 5), Of(plan, "X"));
        Assert.Equal(new BotPlacement("Y", BotSeat.In, 5), Of(plan, "Y"));
        Assert.Equal(BotSeat.Out, Of(plan, "Z").Seat);
    }

    [Fact]
    public void match_size_zero_takes_every_bot_out()
    {
        var plan = BotDirector.Plan([Bot("A"), Bot("B")], Map((0, 0)), Map());
        Assert.All(plan, p => Assert.Equal(BotSeat.Out, p.Seat));
    }

    [Fact]
    public void more_players_than_the_size_takes_every_bot_out_and_moves_no_player()
    {
        var bots = new[] { Bot("A", 0), Bot("B", 0), Bot("C", 1) };
        var plan = BotDirector.Plan(bots, Map((0, 2), (1, 1)), Map((0, 5)));
        Assert.Equal(BotSeat.Out, Of(plan, "A").Seat);
        Assert.Equal(BotSeat.Out, Of(plan, "B").Seat);
        Assert.Equal(new BotPlacement("C", BotSeat.In, 1), Of(plan, "C"));
    }

    [Fact]
    public void bots_that_are_not_deployed_take_no_part()
    {
        var plan = BotDirector.Plan([Bot("A"), Bot("B", deployed: false)], Map((0, 2)), Map());
        Assert.Equal("A", Assert.Single(plan).Label);
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    private static BotPlacement[] Target(BotSeat seat, int team = 0) => [new("Bob", seat, team)];

    /// <summary>Steps once a second until the bot's seat equals <paramref name="seat"/>; returns elapsed seconds.</summary>
    private static int Wait(Clock clock, BotSeats seats, BotSeat seat, BotPlacement[] target, int limit = 200)
    {
        for (int s = 0; s <= limit; s++)
        {
            if (seats.Step(target).Single().Seat == seat) return s;
            clock.Now += TimeSpan.FromSeconds(1);
        }
        throw new Xunit.Sdk.XunitException("never reached " + seat);
    }

    private static (Clock, BotSeats) Seated(int seed)
    {
        var clock = new Clock();
        var seats = new BotSeats(clock.Read, new Random(seed));
        Wait(clock, seats, BotSeat.In, Target(BotSeat.In));
        clock.Now += TimeSpan.FromSeconds(20);
        return (clock, seats);
    }

    [Fact]
    public void leaving_spectates_first_then_goes_20_to_60_seconds_later()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var (clock, seats) = Seated(seed);
            var leave = Target(BotSeat.Out);
            Assert.Equal(BotSeat.Spectating, seats.Step(leave).Single().Seat);
            var spectatingAt = clock.Now;
            int s = Wait(clock, seats, BotSeat.Out, leave);
            Assert.InRange(s, 20, 60);
            Assert.Equal(spectatingAt + TimeSpan.FromSeconds(s), clock.Now);
        }
    }

    [Fact]
    public void coming_back_waits_3_to_15_seconds()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var (clock, seats) = Seated(seed);
            Wait(clock, seats, BotSeat.Out, Target(BotSeat.Out));
            clock.Now += TimeSpan.FromSeconds(20);
            Assert.Equal(BotSeat.Out, seats.Step(Target(BotSeat.In)).Single().Seat);
            int s = Wait(clock, seats, BotSeat.In, Target(BotSeat.In));
            Assert.InRange(s, 3, 15);
        }
    }

    [Fact]
    public void a_bot_seen_first_with_target_in_starts_out_and_comes_in_after_3_to_15_seconds()
    {
        var clock = new Clock();
        var seats = new BotSeats(clock.Read, new Random(7));
        Assert.Equal(BotSeat.Out, seats.Step(Target(BotSeat.In)).Single().Seat);
        Assert.InRange(Wait(clock, seats, BotSeat.In, Target(BotSeat.In)), 3, 15);
    }

    [Fact]
    public void a_team_change_spectates_on_the_new_team_then_comes_in()
    {
        var (clock, seats) = Seated(3);
        var moved = seats.Step(Target(BotSeat.In, 1)).Single();
        Assert.Equal(new BotPlacement("Bob", BotSeat.Spectating, 1), moved);
        int s = Wait(clock, seats, BotSeat.In, Target(BotSeat.In, 1));
        Assert.InRange(s, 3, 15);
        Assert.Equal(1, seats.Step(Target(BotSeat.In, 1)).Single().Team);
    }

    [Fact]
    public void a_bot_changes_seat_at_most_once_per_15_seconds()
    {
        var (clock, seats) = Seated(11);
        var last = seats.Step(Target(BotSeat.In)).Single();
        int changes = 0;
        for (int i = 0; i < 30; i++)
        {
            var now = seats.Step(Target(i % 2 == 0 ? BotSeat.Out : BotSeat.In)).Single();
            if (now != last) changes++;
            last = now;
            clock.Now += TimeSpan.FromSeconds(2);
        }
        Assert.InRange(changes, 1, 4);
    }
}
