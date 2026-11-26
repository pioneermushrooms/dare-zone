using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class BotCommandTests
{
    [Theory]
    [InlineData("Sparring", "Sparring", null, null)]
    [InlineData("Sparring fighter 3", "Sparring", "fighter", 3)]
    [InlineData("Sparring 3 fighter", "Sparring", "fighter", 3)]
    [InlineData("fighter Sparring", "Sparring", "fighter", null)]
    [InlineData("Big Bob ship 2 patrol", "Big Bob", "patrol", 2)]
    [InlineData("Sparring behavior sentry", "Sparring", "sentry", null)]
    [InlineData("Sparring Javelin", "Sparring", null, 2)]
    [InlineData("Sparring [bot] idle", "Sparring", "idle", null)]
    [InlineData("Patrol fighter", "Patrol", "fighter", null)]
    [InlineData("Patrol", "Patrol", null, null)]
    [InlineData("", "", null, null)]
    public void reads_a_name_and_options_in_either_order(string text, string label, string? behaviour, int? ship)
    {
        var args = BotCommand.Parse(text);
        Assert.Equal(label, args.Label);
        Assert.Equal(behaviour, args.Behaviour);
        Assert.Equal(ship, args.Ship);
    }

    [Fact]
    public void a_bare_name_drops_the_bot_suffix()
    {
        Assert.Equal("Sparring", BotCommand.Label("Sparring [bot]"));
        Assert.Equal("Big Bob", BotCommand.Label("  Big   Bob "));
    }

    [Fact]
    public void apply_changes_behaviour_and_ship_and_keeps_the_rest()
    {
        Assert.Equal("ship 1\nbehaviour fighter", BotCommand.Apply(null, null, null));
        var script = "ship 3\nbehaviour patrol\npoint 512 512\ngreet Hi {name}";
        Assert.Equal("ship 3\nbehaviour sentry\npoint 512 512\ngreet Hi {name}", BotCommand.Apply(script, "sentry", null));
        Assert.Equal("ship 8\nbehaviour patrol\npoint 512 512\ngreet Hi {name}", BotCommand.Apply(script, null, 8));
    }

    [Fact]
    public void describe_names_the_behaviour_ship_and_waypoints()
    {
        Assert.Equal("fighter, Spider", BotCommand.Describe("ship 3\nbehaviour fighter"));
        Assert.Equal("patrol, Warbird, 2 waypoints", BotCommand.Describe("behaviour patrol\npoint 1 1\npoint 2 2"));
    }

    [Fact]
    public void chat_on_and_off_read_and_apply()
    {
        Assert.True(BotCommand.Parse("Sparring fighter chat on").Chat);
        Assert.Equal("Sparring", BotCommand.Parse("Sparring chat on fighter 3").Label);
        Assert.False(BotCommand.Parse("Sparring chat off").Chat);
        Assert.Null(BotCommand.Parse("Sparring fighter").Chat);
        var on = BotCommand.Apply(null, "fighter", null, true);
        Assert.Contains("casual 2 friendly", on);
        Assert.Equal("fighter, Warbird, chats", BotCommand.Describe(on));
        Assert.Equal(on, BotCommand.Apply(on, null, null, true));
        Assert.DoesNotContain("casual", BotCommand.Apply(on + "\nbotchat on", null, null, false));
    }

    [Fact]
    public void team_and_priority_read_and_apply()
    {
        var args = BotCommand.Parse("Sparring team 2 priority 7");
        Assert.Equal(("Sparring", 2, 7), (args.Label, args.Team, args.Priority));
        var script = BotCommand.Apply(null, null, null, null, 2, 7);
        Assert.Contains("team 2", script);
        Assert.Contains("priority 7", script);
        Assert.Equal("fighter, Warbird, team 2", BotCommand.Describe(script));
        Assert.Equal(script, BotCommand.Apply(script, null, null));
        Assert.Contains("team 4", BotCommand.Apply(script, null, null, null, 4, null));
        Assert.Contains("priority 7", BotCommand.Apply(script, null, null, null, 4, null));
    }

    [Fact]
    public void match_command_reads()
    {
        Assert.Equal((1, 4), BotCommand.ParseMatch("1 4"));
        Assert.Equal((1, (int?)null), BotCommand.ParseMatch("1 off"));
        Assert.Null(BotCommand.ParseMatch("x 4"));
    }
}
