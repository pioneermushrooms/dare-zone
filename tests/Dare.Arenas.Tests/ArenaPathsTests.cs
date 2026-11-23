using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class ArenaPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arenapaths-" + Guid.NewGuid().ToString("N"));

    public ArenaPathsTests() => Directory.CreateDirectory(Path.Combine(_root, "myarena"));

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Plain_name_resolves_inside_arena_dir()
    {
        var p = ArenaPaths.Resolve(_root, "myarena", "map.lvl");
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "myarena", "map.lvl"), p);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc/passwd")]
    [InlineData("a/b.lvl")]
    [InlineData("..\\x")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("   ")]
    public void Bad_names_return_null(string name) =>
        Assert.Null(ArenaPaths.Resolve(_root, "myarena", name));

    [Fact]
    public void Existing_symlink_returns_null()
    {
        var target = Path.Combine(_root, "outside.lvl");
        File.WriteAllText(target, "x");
        File.CreateSymbolicLink(Path.Combine(_root, "myarena", "link.lvl"), target);
        Assert.Null(ArenaPaths.Resolve(_root, "myarena", "link.lvl"));
    }
}
