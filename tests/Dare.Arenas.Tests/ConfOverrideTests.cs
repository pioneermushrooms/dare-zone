using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class ConfOverrideTests
{
    private const string Template = "; comment\n#include conf/svs/svs.conf\n\n[ General ]\nMap = smallmape.lvl\n";

    [Fact]
    public void No_override_yet_means_append()
    {
        Assert.False(ConfOverride.CanUpdateInPlace(Template, "Warbird", "BulletSpeed"));
    }

    [Fact]
    public void Own_override_after_the_includes_can_be_updated()
    {
        var text = Template + "; Asker with Dare.Arenas\nWarbird:BulletSpeed = 3000\n";
        Assert.True(ConfOverride.CanUpdateInPlace(text, "Warbird", "BulletSpeed"));
        Assert.True(ConfOverride.CanUpdateInPlace(text.Replace("\n", "\r\n"), "warbird", "bulletspeed"));
    }

    [Fact]
    public void Other_keys_do_not_count()
    {
        var text = Template + "Warbird:BulletSpeedX = 1\nJavelin:BulletSpeed = 2\nWarbird:BulletSpeed2=3\n";
        Assert.False(ConfOverride.CanUpdateInPlace(text, "Warbird", "BulletSpeed"));
    }

    [Theory]
    [InlineData("#include conf/other.conf\n")]
    [InlineData("#endif\n")]
    [InlineData("%include x\n")]
    [InlineData("#if FOO\n")]
    public void A_preprocessor_line_after_the_override_means_append(string after)
    {
        var text = Template + "Warbird:BulletSpeed = 3000\n" + after;
        Assert.False(ConfOverride.CanUpdateInPlace(text, "Warbird", "BulletSpeed"));
    }

    [Fact]
    public void Comments_and_settings_after_the_override_are_fine()
    {
        var text = Template + "Warbird:BulletSpeed = 3000\n; note\nJavelin:BulletSpeed = 1\n[ Misc ]\nX = 1\n";
        Assert.True(ConfOverride.CanUpdateInPlace(text, "Warbird", "BulletSpeed"));
    }

    [Fact]
    public void Commented_out_override_does_not_count()
    {
        var text = Template + ";Warbird:BulletSpeed = 3000\n";
        Assert.False(ConfOverride.CanUpdateInPlace(text, "Warbird", "BulletSpeed"));
    }

    [Fact]
    public void Empty_or_null_text_means_append()
    {
        Assert.False(ConfOverride.CanUpdateInPlace("", "Warbird", "BulletSpeed"));
        Assert.False(ConfOverride.CanUpdateInPlace(null, "Warbird", "BulletSpeed"));
    }
}
