using System.Text;
using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class SettingSpecTests
{
    private enum Mode { All, LeftRight, TopBottom }

    [Flags]
    private enum Notify { None = 0, Player = 1, Arena = 2 }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("yes", "1")]
    [InlineData("On", "1")]
    [InlineData("0", "0")]
    [InlineData("no", "0")]
    [InlineData("false", "0")]
    public void Bools_take_1_0_and_yes_no_words(string raw, string expected)
    {
        Assert.True(SettingSpec.From(typeof(bool), null, null).TryCheck("Soccer:BallBounce", raw, out var v, out _));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("maybe")]
    public void Bools_refuse_anything_else(string raw)
    {
        Assert.False(SettingSpec.From(typeof(bool), null, null).TryCheck("Soccer:BallBounce", raw, out _, out var error));
        Assert.Contains("1 or 0", error);
    }

    [Fact]
    public void Integers_are_held_to_their_type()
    {
        var spec = SettingSpec.From(typeof(short), null, null);
        Assert.True(spec.TryCheck("Warbird:BulletSpeed", "+0300", out var v, out _));
        Assert.Equal("300", v);
        Assert.False(spec.TryCheck("Warbird:BulletSpeed", "40000", out _, out var error));
        Assert.Contains("-32768 to 32767", error);
        Assert.False(spec.TryCheck("Warbird:BulletSpeed", "fast", out _, out error));
        Assert.Contains("whole number", error);
        Assert.False(SettingSpec.From(typeof(byte), null, null).TryCheck("Warbird:Gravity", "-5", out _, out _));
    }

    [Fact]
    public void Integers_are_held_to_the_help_min_and_max()
    {
        var spec = SettingSpec.From(typeof(short), "1", null);
        Assert.False(spec.TryCheck("Misc:SendPositionDelay", "0", out _, out var error));
        Assert.Contains("from 1 to 32767", error);
        Assert.True(spec.TryCheck("Misc:SendPositionDelay", "1", out _, out _));
        Assert.Equal("1 to 32767", spec.Describe());
    }

    [Fact]
    public void Enums_take_a_choice_name_or_its_number()
    {
        var spec = SettingSpec.From(typeof(Mode), null, null);
        Assert.True(spec.TryCheck("Soccer:Mode", "leftright", out var v, out _));
        Assert.Equal("LeftRight", v);
        Assert.True(spec.TryCheck("Soccer:Mode", "2", out v, out _));
        Assert.Equal("2", v);
        Assert.False(spec.TryCheck("Soccer:Mode", "7", out _, out var error));
        Assert.Contains("All, LeftRight, TopBottom", error);
    }

    [Fact]
    public void Flags_take_names_or_a_number_of_known_bits()
    {
        var spec = SettingSpec.From(typeof(Notify), null, null);
        Assert.True(spec.TryCheck("Replay:NotifyPlayback", "player, ARENA", out var v, out _));
        Assert.Equal("Player, Arena", v);
        Assert.True(spec.TryCheck("Replay:NotifyPlayback", "3", out v, out _));
        Assert.False(spec.TryCheck("Replay:NotifyPlayback", "4", out _, out _));
        Assert.False(spec.TryCheck("Replay:NotifyPlayback", "Zone", out _, out _));
    }

    [Fact]
    public void Text_and_unknown_types_take_anything()
    {
        Assert.True(SettingSpec.From(typeof(string), null, null).TryCheck("Misc:GreetMessage", "hi", out _, out _));
        Assert.True(SettingSpec.From(null, null, null).TryCheck("X:Y", "anything", out _, out _));
    }
}

public class QuickfixFileTests
{
    [Fact]
    public void Writes_a_server_set_line_and_keeps_colons_in_the_help()
    {
        Assert.True(QuickfixFile.TryLine(new("Soccer", "Mode", "All", "", "", "Goal configuration:\n All = any"), out var line));
        Assert.Equal("Soccer:Mode:All:::Goal configuration: All = any\r\n", line);
    }

    [Fact]
    public void Refuses_a_line_whose_value_holds_a_colon()
    {
        Assert.False(QuickfixFile.TryLine(new("Misc", "GreetMessage", "Hi: welcome", "", "", ""), out _));
    }

    [Fact]
    public void Parses_changes_and_the_perm_pragma()
    {
        var body = Encoding.ASCII.GetBytes("Warbird:BulletSpeed:3000\0__pragma:perm:0\0Misc:GreetMessage:a:b\0");
        Assert.True(QuickfixFile.TryParseChanges(body, Encoding.ASCII, out var changes, out var permanent));
        Assert.False(permanent);
        Assert.Equal([new("Warbird:BulletSpeed", "3000"), new("Misc:GreetMessage", "a:b")], changes);
    }

    [Theory]
    [InlineData("nocolons\0")]
    [InlineData(":Key:1\0")]
    [InlineData("Section::1\0")]
    public void Refuses_malformed_changes(string text)
    {
        Assert.False(QuickfixFile.TryParseChanges(Encoding.ASCII.GetBytes(text), Encoding.ASCII, out _, out _));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("bullet", true)]
    [InlineData("WARBIRD", true)]
    [InlineData("shark", false)]
    public void Filters_by_section_or_key(string filter, bool expected) =>
        Assert.Equal(expected, QuickfixFile.Matches(filter, "Warbird", "BulletSpeed"));
}
