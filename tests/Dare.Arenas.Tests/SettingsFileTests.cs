using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// Settings files dropped on the arena editor: arena.conf, .cfg/.ini and Continuum-style .set text.
public sealed class SettingsFileTests
{
    private static Dictionary<string, string> Map(string text) =>
        SettingsFile.Parse(text).Entries.ToDictionary(e => $"{e.Section}:{e.Key}", e => e.Value);

    [Fact]
    public void reads_sections_and_key_value_lines()
    {
        var m = Map("""
            ; a comment
            / another
            [Warbird]
            BulletSpeed = 2600
            InitialEnergy=1100

            [Flag]
              CarryFlags =  2
            """);
        Assert.Equal(new Dictionary<string, string>
        {
            ["Warbird:BulletSpeed"] = "2600", ["Warbird:InitialEnergy"] = "1100", ["Flag:CarryFlags"] = "2",
        }, m);
    }

    [Fact]
    public void reads_section_colon_key_lines_in_both_forms()
    {
        var m = Map("Warbird:BulletSpeed=1\nFlag:CarryFlags:3\nMisc:GreetMessage = Hi there: welcome");
        Assert.Equal("1", m["Warbird:BulletSpeed"]);
        Assert.Equal("3", m["Flag:CarryFlags"]);
        Assert.Equal("Hi there: welcome", m["Misc:GreetMessage"]);
    }

    [Fact]
    public void all_section_expands_to_every_ship()
    {
        var m = Map("[All]\nMaxBombs=3");
        Assert.Equal(8, m.Count);
        Assert.All(SettingsFile.Ships, s => Assert.Equal("3", m[$"{s}:MaxBombs"]));
    }

    [Fact]
    public void last_value_wins_and_order_is_kept()
    {
        var (entries, _) = SettingsFile.Parse("[A]\nX=1\nY=2\n[a]\nx=3");
        Assert.Equal(["A:X", "A:Y"], entries.Select(e => $"{e.Section}:{e.Key}"));
        Assert.Equal("3", entries[0].Value);
        Assert.Equal(5, entries[0].Line);
    }

    [Fact]
    public void lists_what_it_could_not_read()
    {
        var (entries, skipped) = SettingsFile.Parse("#include conf/svs/svs.conf\nOrphan=1\n[Bad Section]\nX=1\n[Ok]\nno value here\nBad Key=2\n#define X 1\n# plain comment");
        Assert.Empty(entries);
        Assert.Equal([1, 2, 3, 4, 6, 7, 8], skipped.Select(s => s.Line));
        Assert.Contains("#include", skipped[0].Reason);
        Assert.Contains("[Section]", skipped[1].Reason);
    }

    [Fact]
    public void handles_bom_and_windows_line_ends()
    {
        var m = Map("﻿[Warbird]\r\nBulletSpeed=5\r\n");
        Assert.Equal("5", m["Warbird:BulletSpeed"]);
    }

    [Fact]
    public void caps_the_number_of_settings()
    {
        var text = "[S]\n" + string.Join("\n", Enumerable.Range(0, SettingsFile.MaxEntries + 3).Select(i => $"K{i}=1"));
        var (entries, skipped) = SettingsFile.Parse(text);
        Assert.Equal(SettingsFile.MaxEntries, entries.Count);
        Assert.Equal(3, skipped.Count);
    }
}
