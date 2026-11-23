using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

/// <summary>Global staff (anything above default in [(global)]) never get arena groups.</summary>
public class GlobalStaffGrantTests
{
    [Theory]
    [InlineData("mod")]
    [InlineData("smod")]
    [InlineData("sysop")]
    [InlineData("Mod")]
    [InlineData("bot")]
    public void global_staff_owner_or_comod_gets_no_arena_group(string global)
    {
        Assert.Null(GrantLogic.GroupToApply("owner", global, global));
        Assert.Null(GrantLogic.GroupToApply("comod", global, global));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void players_without_a_global_group_are_granted_as_before(string? global)
    {
        Assert.Equal("arenaowner", GrantLogic.GroupToApply("owner", "default", global));
        Assert.Equal("arenamod", GrantLogic.GroupToApply("comod", "default", global));
        Assert.Null(GrantLogic.GroupToApply("owner", "arenaowner", global));
    }

    [Theory]
    [InlineData("arenaowner")]
    [InlineData("arenamod")]
    public void a_module_group_left_on_global_staff_is_put_back_to_their_global_group(string current)
    {
        // granted before this rule existed: heal it to the global group, role or not
        Assert.Equal("mod", GrantLogic.GroupToApply("owner", current, "mod"));
        Assert.Equal("smod", GrantLogic.GroupToApply(null, current, "smod"));
    }

    [Fact]
    public void global_staff_with_their_own_group_in_the_arena_is_left_alone()
    {
        Assert.Null(GrantLogic.GroupToApply("owner", "mod", "mod"));
        Assert.Null(GrantLogic.GroupToApply(null, "default", "mod")); // e.g. an old revoke wrote default
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("default", "default")]
    [InlineData("mod", "mod")]
    [InlineData("sysop", "sysop")]
    public void revoke_writes_the_global_group_instead_of_default(string? global, string expected)
    {
        Assert.Equal(expected, GrantLogic.RevokeTo(global));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("default", false)]
    [InlineData("mod", true)]
    [InlineData("arenaowner", true)]
    public void is_global_staff(string? global, bool expected)
    {
        Assert.Equal(expected, GrantLogic.IsGlobalStaff(global));
    }
}
