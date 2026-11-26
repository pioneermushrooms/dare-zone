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
}
