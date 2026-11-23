using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

/// <summary>Co-mod and transfer targets must be names someone has claimed with a password.</summary>
public class ClaimedNameTests
{
    private const string Passwd = """
        [General]
        AllowUnknown = yes
        [Users]
        ; New users password hashes will get added here.
        Tester = j/R2rkQcXaNPBIzu6F+LXH==
        Big Chris = abc123==
        Locked = lock
        Anyone = any
        users:Late Comer = zzz==
        ; Ghost = commented==
        [Other]
        Elsewhere = qqq==
        """;

    [Theory]
    [InlineData("hash==", true)]
    [InlineData("any", false)]
    [InlineData("ANY", false)]
    [InlineData("lock", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void a_claim_is_a_real_password_hash(string? value, bool claimed)
    {
        Assert.Equal(claimed, ClaimedName.IsClaim(value));
    }

    [Theory]
    [InlineData("tester", "Tester")]
    [InlineData("big chris", "Big Chris")]
    [InlineData("late comer", "Late Comer")]
    [InlineData("locked", null)]
    [InlineData("anyone", null)]
    [InlineData("ghost", null)]
    [InlineData("elsewhere", null)]
    [InlineData("nobody", null)]
    public void canonical_name_comes_from_the_users_section(string typed, string? expected)
    {
        Assert.Equal(expected, ClaimedName.Canonical(Passwd, typed));
    }

    [Fact]
    public void online_authenticated_player_is_accepted_with_their_spelling()
    {
        Assert.Equal("Pal", ClaimedName.Resolve("pal", onlineAuthenticated: "Pal", passwdValue: null, passwdText: null));
    }

    [Fact]
    public void offline_claimed_name_uses_the_stored_spelling()
    {
        Assert.Equal("Big Chris", ClaimedName.Resolve("BIG CHRIS", null, "abc123==", Passwd));
    }

    [Fact]
    public void claimed_in_memory_but_not_yet_on_disk_keeps_the_typed_spelling()
    {
        Assert.Equal("newbie", ClaimedName.Resolve(" newbie ", null, "fresh==", Passwd));
        Assert.Equal("newbie", ClaimedName.Resolve("newbie", null, "fresh==", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("any")]
    [InlineData("lock")]
    public void unclaimed_names_are_refused(string? value)
    {
        Assert.Null(ClaimedName.Resolve("someone", null, value, Passwd));
    }
}
