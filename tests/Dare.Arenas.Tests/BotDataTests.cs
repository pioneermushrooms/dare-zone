using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class BotDataTests
{
    private const string M = "\tbots\t";

    private static readonly IReadOnlyList<BotData.Row> Two =
    [
        new("Big Bob", "fighter", 3, 0, 5, true, true, "in", null),
        new("Patrol", "patrol", 1, 2, 9, false, false, "stopped", null),
    ];

    [Fact]
    public void renders_head_bots_matches_and_end_in_order()
    {
        var lines = BotData.Render("botlab", owner: true, Two, new Dictionary<int, int> { [2] = 4, [0] = 3 });
        Assert.Equal(
        [
            M + "head\tbotlab\tyes",
            M + "bot\tBig Bob\tfighter\t3\t0\t5\ton\tyes\tin\t",
            M + "bot\tPatrol\tpatrol\t1\t2\t9\toff\tno\tstopped\t",
            M + "match\t0\t3",
            M + "match\t2\t4",
            M + "end",
        ], lines);
    }

    [Fact]
    public void bot_lines_survive_names_with_spaces()
    {
        var row = new BotData.Row("Big Bob", "sentry", 8, 9999, 1, false, true, "not launched", "Not launched: a pilot uses this name");
        var line = Assert.Single(BotData.Render("a", true, [row], new Dictionary<int, int>()), l => l.Contains("\tbot\t") || l.StartsWith(M + "bot\t"));
        Assert.Equal(M + "bot\tBig Bob\tsentry\t8\t9999\t1\toff\tyes\tnot launched\tNot launched: a pilot uses this name", line);
    }

    [Fact]
    public void a_non_owner_sees_no_problem_text()
    {
        var row = new BotData.Row("X", "idle", 1, 0, 5, false, true, "not launched", "Not launched: a pilot uses this name");
        var lines = BotData.Render("a", owner: false, [row], new Dictionary<int, int>());
        Assert.Equal(M + "head\ta\tno", lines[0]);
        Assert.Equal(M + "bot\tX\tidle\t1\t0\t5\toff\tyes\tnot launched\t", lines[1]);
    }

    [Fact]
    public void every_line_starts_with_the_marker() =>
        Assert.All(BotData.Render("a", true, Two, new Dictionary<int, int> { [1] = 2 }), l => Assert.StartsWith(M, l));

    [Fact]
    public void a_row_reads_behaviour_ship_team_priority_and_chat_from_the_script()
    {
        var row = BotData.FromScript("Sparring", "ship 3\nbehaviour patrol\nteam 2\npriority 8\ncasual 2 friendly\npoint 1 1", true, "in", null);
        Assert.Equal(new BotData.Row("Sparring", "patrol", 3, 2, 8, true, true, "in", null), row);
        var bare = BotData.FromScript("Plain", "behaviour fighter", false, "stopped", null);
        Assert.Equal(new BotData.Row("Plain", "fighter", 1, 0, 5, false, false, "stopped", null), bare);
    }

    [Fact]
    public void the_windows_create_command_reads_back()
    {
        var args = BotCommand.Parse("Big Bob sentry ship 4 team 3 chat on");
        Assert.Equal(("Big Bob", "sentry", 4, 3, true), (args.Label, args.Behaviour, args.Ship, args.Team, args.Chat));
    }
}
