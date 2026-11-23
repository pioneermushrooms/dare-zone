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
    public void Refuses_empty_control_and_semicolon(string? raw)
    {
        Assert.False(SettingValue.TryValidate(raw, out var value, out var error));
        Assert.Equal("", value);
        Assert.NotEqual("", error);
    }
}
