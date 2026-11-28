using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class SettingRefTests
{
    [Theory]
    [InlineData("Bullet:BulletSpeed", "Bullet", "BulletSpeed")]
    [InlineData("  Misc:Some_Key2 ", "Misc", "Some_Key2")]
    [InlineData("a:b", "a", "b")]
    [InlineData("Spawn:Team0-X", "Spawn", "Team0-X")] // the server's team spawn keys have a dash
    public void Parses_valid_references(string text, string section, string key)
    {
        Assert.True(SettingRef.TryParse(text, out var s, out var k));
        Assert.Equal(section, s);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bullet")]
    [InlineData(":BulletSpeed")]
    [InlineData("Bullet:")]
    [InlineData("a:b:c")]
    [InlineData("Bul let:Speed")]
    [InlineData("Bullet: Speed")]
    [InlineData("Bullet:Speed;x")]
    [InlineData("../x:y")]
    [InlineData("Bullet:Spé")]
    [InlineData("Bullet:Sp\needd")]
    [InlineData("Sec-tion:Key")]
    [InlineData("Spawn:-X")]
    public void Rejects_invalid_references(string text)
    {
        Assert.False(SettingRef.TryParse(text, out var s, out var k));
        Assert.Equal("", s);
        Assert.Equal("", k);
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.False(SettingRef.TryParse(null!, out _, out _));
    }
}
