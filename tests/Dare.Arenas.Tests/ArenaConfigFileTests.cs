using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

/// <summary>
/// The riskiest invariant: owner edits are only ever written to arenas/&lt;name&gt;/arena.conf, never
/// to a file the config search path falls back to (conf/arena.conf, ./arena.conf,
/// arenas/(default)/arena.conf), which every arena without its own file shares.
/// </summary>
public sealed class ArenaConfigFileTests : IDisposable
{
    private readonly string _zone = Path.Combine(Path.GetTempPath(), $"dare-cfg-{Guid.NewGuid():N}");
    private string Arenas => Path.Combine(_zone, "arenas");

    public ArenaConfigFileTests()
    {
        // every shared fallback exists, as on a real zone
        Directory.CreateDirectory(Path.Combine(_zone, "conf"));
        File.WriteAllText(Path.Combine(_zone, "conf", "arena.conf"), "[General]\n");
        File.WriteAllText(Path.Combine(_zone, "arena.conf"), "[General]\n");
        Directory.CreateDirectory(Path.Combine(Arenas, "(default)"));
        File.WriteAllText(Path.Combine(Arenas, "(default)", "arena.conf"), "[General]\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_zone, true); } catch { }
    }

    [Fact]
    public void own_file_is_the_target()
    {
        Directory.CreateDirectory(Path.Combine(Arenas, "skyrace"));
        File.WriteAllText(Path.Combine(Arenas, "skyrace", "arena.conf"), "[General]\n");
        Assert.Equal(Path.GetFullPath(Path.Combine(Arenas, "skyrace", "arena.conf")), ArenaConfigFile.OwnPath(Arenas, "skyrace"));
    }

    [Fact]
    public void no_own_file_means_no_target_never_a_shared_fallback()
    {
        // approved while occupied / folder copy failed / revive could not restore: no own file
        Assert.Null(ArenaConfigFile.OwnPath(Arenas, "skyrace"));
        Directory.CreateDirectory(Path.Combine(Arenas, "skyrace")); // folder without arena.conf
        Assert.Null(ArenaConfigFile.OwnPath(Arenas, "skyrace"));
    }

    [Theory]
    [InlineData("(default)")]
    [InlineData("..")]
    [InlineData("../conf")]
    [InlineData("")]
    [InlineData("Sky race")]
    [InlineData("conf/..")]
    public void names_that_are_not_plain_arena_names_have_no_target(string name)
    {
        Assert.Null(ArenaConfigFile.OwnPath(Arenas, name));
    }

    [Fact]
    public void a_symlinked_arena_conf_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(Arenas, "skyrace"));
        File.CreateSymbolicLink(Path.Combine(Arenas, "skyrace", "arena.conf"), Path.Combine(_zone, "conf", "arena.conf"));
        Assert.Null(ArenaConfigFile.OwnPath(Arenas, "skyrace"));
    }

    [Theory]
    [InlineData("3000", "3000", true)]
    [InlineData("2000", "3000", false)]
    [InlineData(null, "3000", false)]
    public void a_change_is_live_only_when_the_running_arena_sees_the_written_value(string? running, string written, bool live)
    {
        Assert.Equal(live, ArenaConfigFile.IsLive(running, written));
    }
}
