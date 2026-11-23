using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class SettingsPolicyTests
{
    private static SettingsPolicy Make() => new(new[]
    {
        ("Kill", "EnterDelay"),
        ("Modules", "AttachModules"),
        ("General", "Map"),
        ("General", "LevelFiles"),
        ("General", "MaxPlaying"),
        ("General", "ScoreGroup"),
        ("General", "NeedCap"),
        ("Misc", "GreetMessage"),
    });

    [Fact]
    public void Documented_arena_key_is_editable() => Assert.True(Make().IsEditable("Kill", "EnterDelay"));

    [Fact]
    public void Comparison_is_case_insensitive() => Assert.True(Make().IsEditable("kill", "enterdelay"));

    [Fact]
    public void Other_general_key_is_editable() => Assert.True(Make().IsEditable("General", "MaxPlaying"));

    [Fact]
    public void Modules_section_is_denied() => Assert.False(Make().IsEditable("Modules", "AttachModules"));

    [Fact]
    public void General_map_is_denied() => Assert.False(Make().IsEditable("General", "Map"));

    [Fact]
    public void General_levelfiles_is_denied_case_insensitively() => Assert.False(Make().IsEditable("general", "levelfiles"));

    [Fact]
    public void Undocumented_key_is_denied() => Assert.False(Make().IsEditable("Kill", "Nope"));

    // Keys whose value names something outside the arena (score group shared with other
    // arenas; a zone-wide capability name) are never owner-editable, even when documented.
    [Fact]
    public void General_scoregroup_is_denied() => Assert.False(Make().IsEditable("General", "ScoreGroup"));

    [Fact]
    public void General_scoregroup_is_denied_case_insensitively() => Assert.False(Make().IsEditable("general", "SCOREGROUP"));

    [Fact]
    public void General_needcap_is_denied() => Assert.False(Make().IsEditable("General", "NeedCap"));

    [Fact]
    public void Arena_local_text_setting_stays_editable() => Assert.True(Make().IsEditable("Misc", "GreetMessage"));
}

