using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class VoicePhrasesTests
{
    private static VoicePhrases Real() =>
        VoicePhrases.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "phrases.json")));

    [Fact]
    public void Real_table_parses_and_matches_spec_examples()
    {
        var t = Real();
        Assert.True(t.TryGet("gw", out var w));
        Assert.Equal(("Woohoo!", VoiceScope.Global), (w.Text, w.Scope));
        Assert.True(t.TryGet("tr", out var r));
        Assert.Equal(("He's red!", VoiceScope.Team), (r.Text, r.Scope));
        Assert.True(t.TryGet("ml", out var l));
        Assert.Equal("Break left!", l.Text);
        Assert.True(t.TryGet("gtb", out var b));
        Assert.Equal("Is that the best you can do?", b.Text);
        Assert.All(t.Phrases, p => Assert.InRange(p.Sound, 128, 255));
        Assert.Equal(t.Phrases.Count, t.Phrases.Select(p => p.Sound).Distinct().Count());
        Assert.Equal(128, t.Phrases[0].Sound);
    }

    [Fact]
    public void Every_phrase_sits_under_a_menu_and_no_code_is_both()
    {
        var t = Real();
        var menus = t.Menus.Select(m => m.Code).ToHashSet();
        Assert.All(t.Phrases, p => Assert.Contains(p.Code[..^1], menus));
        Assert.All(t.Menus.Where(m => m.Code.Length > 1), m => Assert.Contains(m.Code[..^1], menus));
        Assert.Empty(t.Phrases.Select(p => p.Code).Intersect(menus));
    }

    [Fact]
    public void Lookup_normalises_and_rejects_menus()
    {
        var t = Real();
        Assert.True(t.TryGet("  GW ", out var p));
        Assert.Equal("gw", p.Code);
        Assert.False(t.TryGet("g", out _));
        Assert.False(t.TryGet("zz", out _));
        Assert.False(t.TryGet("", out _));
    }

    [Theory]
    [InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"gy","sound":127,"scope":"global","label":"Y","text":"Y","delivery":"calm"}]}""")] // id out of range
    [InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"gy","sound":128,"scope":"both","label":"Y","text":"Y","delivery":"calm"}]}""")] // bad scope
    [InlineData("""{"menus":[],"phrases":[{"code":"gy","sound":128,"scope":"global","label":"Y","text":"Y","delivery":"calm"}]}""")] // orphan
    [InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"gy","sound":128,"scope":"global","label":"Y","text":"Y","delivery":"calm"},{"code":"gn","sound":128,"scope":"global","label":"N","text":"N","delivery":"calm"}]}""")] // duplicate id
    [InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"gy","sound":128,"scope":"global","label":"Y","text":"Y","delivery":"loud"}]}""")] // unknown delivery
    [InlineData("""{"menus":[{"code":"g","label":"G"}],"phrases":[{"code":"g","sound":128,"scope":"global","label":"Y","text":"Y","delivery":"calm"}]}""")] // code is a menu too
    public void Parse_rejects_bad_tables(string json) =>
        Assert.Throws<FormatException>(() => VoicePhrases.Parse(json));
}
