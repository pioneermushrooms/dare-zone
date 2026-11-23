using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class ArenaNameTests
{
    private static readonly IReadOnlySet<string> NoArenas = new HashSet<string>();

    [Fact]
    public void Valid_name_is_lowercased()
    {
        Assert.True(ArenaName.TryValidate("Pipe-Dreams", NoArenas, out var normalized, out var error));
        Assert.Equal("pipe-dreams", normalized);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopq")]
    [InlineData("turf1")]
    [InlineData("1turf")]
    [InlineData("a b")]
    [InlineData("(x)")]
    [InlineData("public")]
    [InlineData("Public")]
    [InlineData("duel")]
    [InlineData("2v2pub")]
    [InlineData("3v3pub")]
    [InlineData("4v4pub")]
    [InlineData("")]
    public void Invalid_names_are_rejected_with_an_error(string input)
    {
        Assert.False(ArenaName.TryValidate(input, NoArenas, out var normalized, out var error));
        Assert.NotEmpty(error);
        Assert.Equal("", normalized);
    }

    [Fact]
    public void Sixteen_characters_is_allowed()
    {
        Assert.True(ArenaName.TryValidate("abcdefghijklmnop", NoArenas, out _, out _));
    }

    [Fact]
    public void Existing_arena_is_rejected_case_insensitively()
    {
        var existing = new HashSet<string> { "Turf" };
        Assert.False(ArenaName.TryValidate("tURf", existing, out _, out var error));
        Assert.NotEmpty(error);
    }
}
