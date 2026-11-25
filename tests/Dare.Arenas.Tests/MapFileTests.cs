using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class MapFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dare-mapfile-{Guid.NewGuid():N}");
    private string Arenas => Path.Combine(_root, "arenas");
    private string Maps => Path.Combine(_root, "maps");

    public MapFileTests()
    {
        Directory.CreateDirectory(Path.Combine(Arenas, "skyrace"));
        Directory.CreateDirectory(Maps);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void arena_folder_first_then_shared_maps()
    {
        var c = MapFile.Candidates(Arenas, "skyrace", Maps, "skyrace.lvl");
        Assert.Equal(2, c.Count);
        Assert.Equal(Path.Combine(Path.GetFullPath(Arenas), "skyrace", "skyrace.lvl"), c[0]);
        Assert.Equal(Path.Combine(Path.GetFullPath(Maps), "skyrace.lvl"), c[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../secret.lvl")]
    [InlineData("..\\secret.lvl")]
    [InlineData("/etc/passwd.lvl")]
    [InlineData("sub/dir.lvl")]
    [InlineData("map.conf")]
    [InlineData("arena.conf")]
    [InlineData("..lvl")]
    [InlineData("C:map.lvl")]
    [InlineData("a\u0000b.lvl")]
    public void unsafe_or_non_map_names_give_nothing(string? setting) =>
        Assert.Empty(MapFile.Candidates(Arenas, "skyrace", Maps, setting));

    [Fact]
    public void too_long_name_gives_nothing() =>
        Assert.Empty(MapFile.Candidates(Arenas, "skyrace", Maps, new string('a', 70) + ".lvl"));

    [Fact]
    public void links_are_never_candidates()
    {
        var target = Path.Combine(_root, "outside.lvl");
        File.WriteAllBytes(target, [1, 2, 3, 4]);
        File.CreateSymbolicLink(Path.Combine(Maps, "link.lvl"), target);
        File.CreateSymbolicLink(Path.Combine(Arenas, "skyrace", "link.lvl"), target);
        Assert.Empty(MapFile.Candidates(Arenas, "skyrace", Maps, "link.lvl"));
    }

    [Fact]
    public void name_is_trimmed() =>
        Assert.Equal(2, MapFile.Candidates(Arenas, "skyrace", Maps, "  smallmape.lvl ").Count);
}
