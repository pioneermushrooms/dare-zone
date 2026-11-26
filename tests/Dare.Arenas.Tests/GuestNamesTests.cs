using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class GuestNamesTests
{
    [Fact]
    public void every_pair_is_a_valid_name_of_at_most_20_characters()
    {
        foreach (var a in GuestNames.Adjectives)
            foreach (var n in GuestNames.Nouns)
            {
                var name = a + " " + n;
                Assert.True(AccountRules.TryName(name, out _, out var err), $"{name}: {err}");
                Assert.False(AccountRules.IsGuestName(name));
            }
    }

    [Fact]
    public void the_numbered_fallbacks_are_valid_too()
    {
        foreach (var n in GuestNames.Nouns)
            Assert.True(AccountRules.TryName(n + " 999", out _, out var err), err);
    }

    [Fact]
    public void a_taken_name_is_skipped()
    {
        var r = new Random(1);
        var first = GuestNames.Pick(new Random(1).Next);
        var got = GuestNames.PickFree(r.Next, n => n == first);
        Assert.NotNull(got);
        Assert.NotEqual(first, got);
    }

    [Fact]
    public void falls_back_to_a_numbered_name_then_gives_up()
    {
        var got = GuestNames.PickFree(new Random(2).Next, n => !char.IsDigit(n[^1]));
        Assert.Matches(@"^[A-Za-z]+ \d{3}$", got!);
        Assert.Null(GuestNames.PickFree(new Random(3).Next, _ => true));
    }

    [Fact]
    public void names_vary()
    {
        var r = new Random(4);
        Assert.True(Enumerable.Range(0, 50).Select(_ => GuestNames.Pick(r.Next)).Distinct().Count() > 40);
    }
}
