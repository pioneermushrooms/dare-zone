namespace Dare.Arenas.Tests;

public class GroupDefTests
{
    private static string[] Caps(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dare.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllLines(Path.Combine(dir.FullName, "deploy", "groupdef.dir", file))
            .Select(line => line.Trim())
            .Where(line => line.Length != 0 && !line.StartsWith(';'))
            .ToArray();
    }

    /// <summary>
    /// Global staff keep their own group in an arena they own, so they get the owner commands
    /// from arenastaff. An owner command missing there is silently refused for staff owners
    /// (that is how ?bots did nothing for them).
    /// </summary>
    [Fact]
    public void staff_get_every_owner_command()
    {
        var missing = Caps("arenaowner").Where(cap => cap.StartsWith("cmd_", StringComparison.Ordinal))
            .Except(Caps("arenastaff")).ToArray();
        Assert.Empty(missing);
    }

    /// <summary>
    /// The zone puts its bots in [arenabot] on arena entry; excludepopulation keeps them out of the
    /// zone's own player count (ping, zone list, directory), so nothing else has to subtract them.
    /// </summary>
    [Fact]
    public void bots_are_left_out_of_population()
    {
        Assert.Contains("excludepopulation", Caps("arenabot"));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dare.sln"))) dir = dir.Parent;
        var sections = File.ReadAllLines(Path.Combine(dir!.FullName, "deploy", "groupdef-sections.conf")).Select(l => l.Trim()).ToList();
        int at = sections.IndexOf("[arenabot]");
        Assert.True(at >= 0, "[arenabot] section missing");
        var body = sections.Skip(at + 1).TakeWhile(l => !l.StartsWith('[')).ToList();
        Assert.Contains("#include groupdef.dir/default", body);
        Assert.Contains("#include groupdef.dir/arenabot", body);
    }

    /// <summary>Anyone may ask ?bots which pilots here are bots; the command itself keeps management to owners.</summary>
    [Fact]
    public void every_player_can_ask_which_pilots_are_bots()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dare.sln"))) dir = dir.Parent;
        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, "deploy", "default-caps.txt")).Select(l => l.Trim());
        Assert.Contains("cmd_bots", lines);
    }
}
