using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class DraftRulesTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void draft_names_validate_like_arenas()
    {
        Assert.True(DraftRules.TryValidate("#SkyRace", None, out var name, out _));
        Assert.Equal("skyrace", name);
        Assert.True(DraftRules.TryValidate("skyrace", None, out name, out _));
        Assert.Equal("skyrace", name);
        foreach (var bad in new[] { "#", "#1abc", "##abc", "#public", "#abc1", "#ab" })
            Assert.False(DraftRules.TryValidate(bad, None, out _, out _), bad);
    }

    [Fact]
    public void draft_names_fit_the_servers_15_character_limit_with_the_hash()
    {
        Assert.True(DraftRules.TryValidate("abcdefghijklmn", None, out _, out _)); // 14 + '#' = 15
        Assert.False(DraftRules.TryValidate("abcdefghijklmno", None, out _, out var error)); // 15 + '#' = 16
        Assert.Contains("14", error);
    }

    [Fact]
    public void draft_names_are_case_insensitive()
    {
        Assert.False(DraftRules.TryValidate("SKYRACE", new HashSet<string> { "#skyrace" }, out _, out _));
        Assert.False(DraftRules.TryValidate("#SkyRace", new HashSet<string> { "SKYRACE" }, out _, out _));
        Assert.True(DraftRules.IsDraft("#x"));
        Assert.False(DraftRules.IsDraft("x"));
        Assert.Equal("#sky", DraftRules.DraftOf("sky"));
        Assert.Equal("sky", DraftRules.PublicOf("#sky"));
        Assert.Equal("sky", DraftRules.PublicOf("sky"));
    }

    [Fact]
    public void entry_allows_owner_comod_invitee_staff_only()
    {
        Assert.Equal(EntryDecision.Allow, DraftRules.Entry("owner", false, false));
        Assert.Equal(EntryDecision.Allow, DraftRules.Entry("comod", false, false));
        Assert.Equal(EntryDecision.Allow, DraftRules.Entry(null, true, false));
        Assert.Equal(EntryDecision.Allow, DraftRules.Entry(null, false, true));
        Assert.Equal(EntryDecision.SendToPublic, DraftRules.Entry(null, false, false));
    }

    [Fact]
    public void publish_files_are_the_allow_list()
    {
        Assert.Equal(new[] { "arena.conf", "x.lvl", "a.lvz", "b.lvz" },
            DraftRules.PublishFiles("x.lvl", "a.lvz,b.lvz,../c.lvz"));
        Assert.Equal(new[] { "arena.conf" }, DraftRules.PublishFiles(null, null));
        Assert.Equal(new[] { "arena.conf" }, DraftRules.PublishFiles("../x.lvl", ""));
        Assert.Equal(new[] { "arena.conf", "X.lvl", "a.lvz" }, DraftRules.PublishFiles("X.lvl", "a.lvz,A.LVZ,x.lvl")); // names keep their case: the server's disk is case-sensitive
    }

    [Fact]
    public void map_file_has_no_hash()
    {
        Assert.Equal("sky.lvl", DraftRules.MapFileName("#sky"));
        Assert.Equal("sky.lvl", DraftRules.MapFileName("sky"));
    }

    [Fact]
    public void backups_keep_newest_three()
    {
        var names = new[] { "20261124T100500Z", "20261124T100100Z", "20261124T100300Z", "20261124T100200Z", "20261124T100400Z" };
        Assert.Equal(new[] { "20261124T100100Z", "20261124T100200Z" }, DraftRules.BackupsToDelete(names));
        Assert.Empty(DraftRules.BackupsToDelete(names.Take(3)));
    }
}
