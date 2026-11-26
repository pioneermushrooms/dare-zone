using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class SettingValueTests
{
    [Theory]
    [InlineData("3000", "3000")]
    [InlineData("  3000  ", "3000")]
    [InlineData("some text value", "some text value")]
    public void Accepts_and_trims(string raw, string expected)
    {
        Assert.True(SettingValue.TryValidate(raw, out var value, out var error));
        Assert.Equal(expected, value);
        Assert.Equal("", error);
    }

    [Fact]
    public void Accepts_64_chars_refuses_65()
    {
        Assert.True(SettingValue.TryValidate(new string('x', 64), out _, out _));
        Assert.False(SettingValue.TryValidate(new string('x', 65), out _, out var error));
        Assert.Contains("64", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1;2")]
    [InlineData("1\n2")]
    [InlineData("1\r2")]
    [InlineData("1\t2")]
    [InlineData("1\u00002")]
    [InlineData("1\u007f2")]
    [InlineData("x\\")]
    [InlineData("a\\b")]
    public void Refuses_empty_control_and_semicolon(string? raw)
    {
        Assert.False(SettingValue.TryValidate(raw, out var value, out var error));
        Assert.Equal("", value);
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("Misc", "GreetMessage", true)]
    [InlineData("misc", "periodicmessage0", true)]
    [InlineData("Misc", "PeriodicMessage9", true)]
    [InlineData("Misc", "PeriodicMessage10", false)]
    [InlineData("Misc", "PeriodicMessageX", false)]
    [InlineData("Misc", "SeeEnergy", false)]
    [InlineData("Warbird", "GreetMessage", false)]
    public void Knows_the_message_settings(string section, string key, bool expected) =>
        Assert.Equal(expected, SettingValue.IsMessage(section, key));

    [Fact]
    public void A_message_may_be_empty_which_turns_it_off()
    {
        Assert.True(SettingValue.TryValidate("Misc", "PeriodicMessage0", "  ", out var value, out _));
        Assert.Equal("", value);
        Assert.False(SettingValue.TryValidate("Warbird", "BulletSpeed", "", out _, out _));
    }

    [Fact]
    public void A_message_may_be_200_chars_other_values_64()
    {
        var long200 = "5 0 " + new string('x', 196);
        Assert.True(SettingValue.TryValidate("Misc", "GreetMessage", long200, out _, out _));
        Assert.False(SettingValue.TryValidate("Misc", "GreetMessage", long200 + "x", out _, out var error));
        Assert.Contains("200", error);
        Assert.False(SettingValue.TryValidate("Misc", "SeeEnergy", new string('x', 65), out _, out _));
    }

    [Fact]
    public void A_message_still_refuses_semicolons_and_backslashes()
    {
        Assert.False(SettingValue.TryValidate("Misc", "GreetMessage", "hi; there", out _, out _));
        Assert.False(SettingValue.TryValidate("Misc", "GreetMessage", "hi \\ there", out _, out _));
    }
}
