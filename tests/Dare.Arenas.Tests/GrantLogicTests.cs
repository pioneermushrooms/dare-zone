using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class GrantLogicTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-grant-{Guid.NewGuid():N}.db");
    private readonly ArenaStore _store;

    public GrantLogicTests()
    {
        _store = new ArenaStore(_db, () => new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    [Theory]
    [InlineData("owner", "arenaowner")]
    [InlineData("comod", "arenamod")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("sysop", null)]
    [InlineData("OWNER", null)]
    public void group_for_maps_roles(string? role, string? expected)
    {
        Assert.Equal(expected, GrantLogic.GroupFor(role));
    }

    [Fact]
    public void grant_applies_on_entry_after_offline_approval()
    {
        // Approved while the requester was not online: nothing could be granted then.
        Assert.True(_store.Request("Asker", "testland", "my test arena").Ok);
        Assert.True(_store.Approve("testland", "Tester", _ => false).Ok);

        // Later the requester enters the arena (name casing may differ).
        var role = _store.RoleIn("testland", "asker");
        Assert.Equal("owner", role);
        Assert.Equal("arenaowner", GrantLogic.GroupFor(role));
        Assert.Equal("arenaowner", GrantLogic.GroupToApply(role, "default"));
    }

    [Fact]
    public void comod_gets_arenamod_on_entry()
    {
        Assert.True(_store.Request("Asker", "testland", "pitch").Ok);
        Assert.True(_store.Approve("testland", "Tester", _ => false).Ok);
        Assert.True(_store.AddCoMod("testland", "Asker", "Helper").Ok);

        Assert.Equal("arenamod", GrantLogic.GroupToApply(_store.RoleIn("testland", "Helper"), "default"));
    }

    [Fact]
    public void no_grant_when_group_already_set()
    {
        Assert.Null(GrantLogic.GroupToApply("owner", "arenaowner"));
        Assert.Null(GrantLogic.GroupToApply("comod", "ARENAMOD"));
    }

    [Fact]
    public void no_grant_without_role_or_in_suspended_arena()
    {
        Assert.Null(GrantLogic.GroupToApply(null, "default"));
        Assert.True(_store.Request("Asker", "testland", "pitch").Ok);
        Assert.True(_store.Approve("testland", "Tester", _ => false).Ok);
        Assert.True(_store.Suspend("testland", "Tester", "rules").Ok);
        Assert.Null(GrantLogic.GroupToApply(_store.RoleIn("testland", "Asker"), "default"));
    }

    [Theory]
    [InlineData("smod")]
    [InlineData("sysop")]
    [InlineData("bot")]
    public void never_downgrades_higher_staff(string current)
    {
        Assert.Null(GrantLogic.GroupToApply("owner", current));
        Assert.Null(GrantLogic.GroupToApply("comod", current));
    }

    [Fact]
    public void mod_is_upgraded_to_arenaowner_but_not_downgraded_to_arenamod()
    {
        Assert.Equal("arenaowner", GrantLogic.GroupToApply("owner", "mod"));
        Assert.Null(GrantLogic.GroupToApply("comod", "mod"));
    }

    [Fact]
    public void module_groups_follow_the_current_role()
    {
        // arenaowner/arenamod are managed by this module, so a stale grant is corrected.
        Assert.Equal("arenamod", GrantLogic.GroupToApply("comod", "arenaowner"));
        Assert.Equal("arenaowner", GrantLogic.GroupToApply("owner", "arenamod"));
    }
}
