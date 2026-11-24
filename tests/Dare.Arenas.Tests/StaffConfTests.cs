using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class StaffConfTests
{
    private const string Text = """
        ;; comment
        [General]
        UseArenaConfStaffList = false

        [GroupPasswords]
        mod = secretpw

        [(global)]
        dare = sysop
        Moddy = mod   ; trailing

        [testland]
        Helper = smod
        other:Spare = mod
        """;

    [Fact]
    public void names_come_from_every_section_except_general_and_group_passwords()
    {
        var names = StaffConf.Names(Text);
        Assert.Equal(new[] { "dare", "Moddy", "Helper", "Spare" }, names.OrderBy(n => Array.IndexOf(new[] { "dare", "Moddy", "Helper", "Spare" }, n)).ToArray());
        Assert.DoesNotContain("UseArenaConfStaffList", names);
        Assert.DoesNotContain("mod", names);
    }

    [Fact]
    public void empty_text_has_no_names()
    {
        Assert.Empty(StaffConf.Names(""));
        Assert.Empty(StaffConf.Names(null!));
    }

    [Fact]
    public void all_users_keeps_lock_and_any_entries_last_definition_wins()
    {
        var users = ClaimedName.AllUsers("[Users]\nAlice = h1\nBob = lock\nCarl = any\nAlice = h2\n");
        Assert.Equal(new[] { ("Alice", "h2"), ("Bob", "lock"), ("Carl", "any") }, users.ToArray());
    }
}
