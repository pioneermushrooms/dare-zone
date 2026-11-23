using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class ArenaNameTests
{
    private static readonly IReadOnlySet<string> NoArenas = new HashSet<string>();

    [Fact]
    public void Valid_name_is_lowercased()
    {
        Assert.True(ArenaName.TryValidate("PipeDreams", NoArenas, out var normalized, out var error));
        Assert.Equal("pipedreams", normalized);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopq")]
    [InlineData("abcdefghijklmnop")]
    [InlineData("Pipe-Dreams")]
    [InlineData("pipe-dreams")]
    [InlineData("sky-race")]
    [InlineData("skyrace-")]
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
    public void Fifteen_characters_is_allowed()
    {
        Assert.True(ArenaName.TryValidate("abcdefghijklmno", NoArenas, out var normalized, out _));
        Assert.Equal("abcdefghijklmno", normalized);
    }

    [Fact]
    public void Three_characters_is_allowed()
    {
        Assert.True(ArenaName.TryValidate("abc", NoArenas, out _, out _));
    }

    [Theory]
    [InlineData("sky-race")]
    [InlineData("abcdefghijklmnop")]
    public void Error_states_the_rule(string input)
    {
        Assert.False(ArenaName.TryValidate(input, NoArenas, out _, out var error));
        Assert.Contains("3-15", error);
        Assert.Contains("letters and digits", error);
    }

    [Fact]
    public void Existing_arena_is_rejected_case_insensitively()
    {
        var existing = new HashSet<string> { "Turf" };
        Assert.False(ArenaName.TryValidate("tURf", existing, out _, out var error));
        Assert.NotEmpty(error);
    }
}
