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
}
