using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// GameMode:Attach: the allow-list of modules a game mode may attach, and what to attach/detach.
public sealed class GameModeAttachTests
{
    private const string Kill = "SS.Core.Modules.Scoring.KillPoints";

    [Fact]
    public void parse_names_any_case_and_separator() =>
        Assert.Equal(GameModeModules.Kills | GameModeModules.Flags, GameModeAttach.Parse("kills, FLAGS", out _));

    [Fact]
    public void parse_ignores_unknown_and_accepts_bits()
    {
        Assert.Equal(GameModeModules.Kills, GameModeAttach.Parse("Kills Bogus", out var unknown));
        Assert.Equal(["Bogus"], unknown);
        Assert.Equal(GameModeModules.Balls | GameModeModules.King, GameModeAttach.Parse("12", out _));
        Assert.Equal(GameModeModules.None, GameModeAttach.Parse(null, out _));
        Assert.Equal(GameModeModules.None, GameModeAttach.Parse("999", out _));
        Assert.Equal(GameModeModules.None, GameModeAttach.Parse("None", out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void type_names_follow_the_allow_list() =>
        Assert.Equal([Kill, "SS.Core.Modules.Enforcers.ShipChange"],
            GameModeAttach.TypeNames(GameModeModules.Kills | GameModeModules.ShipChange));

    [Fact]
    public void diff_attaches_new_and_detaches_removed()
    {
        var ours = new HashSet<string>([Kill, "SS.Core.Modules.Scoring.Koth"]);
        var (a, d) = GameModeAttach.Diff(ours, GameModeModules.Kills | GameModeModules.Flags, new HashSet<string>());
        Assert.Equal([FlagHold.TypeName], a);
        Assert.Equal(["SS.Core.Modules.Scoring.Koth"], d);
    }

    [Fact]
    public void never_detaches_staff_attached()
    {
        var staff = GameModeAttach.StaffAttached("SS.Core.Modules.Scoring.KillPoints \\\n\tSS.Core.Modules.Buy");
        Assert.Contains(Kill, staff);
        var (a, _) = GameModeAttach.Diff(new HashSet<string>(), GameModeModules.Kills, staff);
        Assert.Empty(a);
        var (_, d) = GameModeAttach.Diff(new HashSet<string>([Kill]), GameModeModules.None, staff);
        Assert.Empty(d);
    }

    [Fact]
    public void failed_attach_is_forgotten_and_failed_detach_is_kept()
    {
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Kill };
        GameModeAttach.Settle(ours, Kill, attaching: true, ok: false);   // e.g. staff attached it by command first
        Assert.DoesNotContain(Kill, ours);                             // so removing Kills later won't detach theirs
        GameModeAttach.Settle(ours, Kill, attaching: false, ok: false);  // a detach that failed: still ours
        Assert.Contains(Kill, ours);
        GameModeAttach.Settle(ours, Kill, attaching: false, ok: true);
        Assert.Contains(Kill, ours);                                   // a success changes nothing (already planned)
    }

    // Koth and BallGamePoints read their settings on the arena's Create action (BallGamePoints only then), which an
    // arena already has when GameMode:Attach attaches them, so they never started: they need Create replayed.
    [Fact]
    public void koth_and_ball_scoring_need_create_replayed()
    {
        Assert.True(GameModeAttach.NeedsCreate("SS.Core.Modules.Scoring.Koth"));
        Assert.True(GameModeAttach.NeedsCreate("SS.Core.Modules.Scoring.BallGamePoints"));
        Assert.False(GameModeAttach.NeedsCreate("SS.Core.Modules.Scoring.KillPoints"));
        Assert.False(GameModeAttach.NeedsCreate(FlagHold.TypeName));
    }
}

