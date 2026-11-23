using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class LevelFilesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Adds_to_empty(string? current)
    {
        Assert.True(LevelFiles.Add(current, "gfx.lvz", 10, out var updated, out var error));
        Assert.Equal("gfx.lvz", updated);
        Assert.Equal("", error);
    }

    [Fact]
    public void Appends_and_normalises_whitespace_and_empty_entries()
    {
        Assert.True(LevelFiles.Add(" a.lvz , ,b.lvz,", "c.lvz", 10, out var updated, out _));
        Assert.Equal("a.lvz,b.lvz,c.lvz", updated);
    }

    [Fact]
    public void Trims_new_name()
    {
        Assert.True(LevelFiles.Add("a.lvz", "  b.lvz ", 10, out var updated, out _));
        Assert.Equal("a.lvz,b.lvz", updated);
    }

    [Fact]
    public void Refuses_case_insensitive_duplicate()
    {
        Assert.False(LevelFiles.Add("a.lvz,Gfx.LVZ", "gfx.lvz", 10, out var updated, out var error));
        Assert.Equal("a.lvz,Gfx.LVZ", updated);
        Assert.Contains("already", error);
    }

    [Fact]
    public void Refuses_when_at_max()
    {
        var current = string.Join(",", Enumerable.Range(1, 10).Select(i => $"f{i}.lvz"));
        Assert.False(LevelFiles.Add(current, "new.lvz", 10, out var updated, out var error));
        Assert.Equal(current, updated);
        Assert.Contains("10", error);
    }

    [Fact]
    public void Allows_up_to_max()
    {
        var current = string.Join(",", Enumerable.Range(1, 9).Select(i => $"f{i}.lvz"));
        Assert.True(LevelFiles.Add(current, "new.lvz", 10, out var updated, out _));
        Assert.Equal(10, LevelFiles.Parse(updated).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a,b.lvz")]
    public void Refuses_empty_or_comma_names(string name)
    {
        Assert.False(LevelFiles.Add("x.lvz", name, 10, out var updated, out var error));
        Assert.Equal("x.lvz", updated);
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Parse_counts_entries()
    {
        Assert.Empty(LevelFiles.Parse(null));
        Assert.Empty(LevelFiles.Parse(" , "));
        Assert.Equal(new[] { "a.lvz", "b.lvz" }, LevelFiles.Parse(" a.lvz ,b.lvz,"));
    }

    [Theory]
    [InlineData("gfx.lvz")]
    [InlineData("My_Gfx-2.LVZ")]
    public void Safe_names(string name) => Assert.True(LevelFiles.IsSafeFileName(name));

    [Theory]
    [InlineData("")]
    [InlineData("a b.lvz")]
    [InlineData("a;b.lvz")]
    [InlineData("a,b.lvz")]
    [InlineData("..lvz")]
    [InlineData(".lvz")]
    [InlineData("../x.lvz")]
    [InlineData("é.lvz")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789.lvz")]
    public void Unsafe_names(string name) => Assert.False(LevelFiles.IsSafeFileName(name));
}
