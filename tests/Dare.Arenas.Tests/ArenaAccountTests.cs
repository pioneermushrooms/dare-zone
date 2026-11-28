using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Arenas belong to the account behind a pilot name: every name of it has the owner's or a co-mod's
// powers, limits count per account, a deleted name hands its entries to another name of the account,
// and an account at its limit can ask staff for more.
public sealed class ArenaAccountTests : IDisposable
{
    private const string Pw = "hunter22pw";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-arena-account-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly ArenaStore _a;
    private readonly AccountStore _s;
    private readonly Account _dare;

    public ArenaAccountTests()
    {
        _a = new ArenaStore(_db, () => _now);
        _s = new AccountStore(_db, () => _now);
        Assert.True(_s.StartSignup("dare", Pw, "dare@example.com", out var code).Ok);
        Assert.True(_s.VerifySignup("dare@example.com", code, out var dare).Ok);
        _dare = dare!;
        Assert.True(_s.AddPilot(_dare.Id, "nerd", out _).Ok);
        Assert.True(_s.CreateWithoutEmail("pal", Pw, out _).Ok);
    }

    public void Dispose()
    {
        _s.Dispose();
        _a.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var x in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal", _db + ".before-persons" })
            try { File.Delete(x); } catch { }
    }

    private void Make(string arena, string owner) =>
        Assert.True(_a.Create(arena, "staff", owner, _ => false, () => null).Ok);

    [Fact]
    public void every_name_of_the_account_is_owner_and_co_mod()
    {
        Make("sky", "dare");
        Make("palsarena", "pal");
        Assert.True(_a.AddCoMod("palsarena", "pal", "dare").Ok);
        Assert.Equal("owner", _a.RoleIn("sky", "NERD"));
        Assert.Equal("comod", _a.RoleIn("palsarena", "nerd"));
        Assert.Null(_a.RoleIn("sky", "pal"));
        // the owner's other name may run co-mods; it can't be made a co-mod of its own arena
        Assert.True(_a.AddCoMod("sky", "nerd", "pal").Ok);
        Assert.False(_a.AddCoMod("sky", "dare", "nerd").Ok);
        Assert.False(_a.AddCoMod("palsarena", "pal", "nerd").Ok); // already co-mod as dare
        Assert.Equal(new[] { "sky" }, _a.OwnedArenas("nerd").Select(a => a.Name));
        Assert.Contains(("sky", "owner"), _a.RolesFor("nerd"));
        Assert.Contains(("palsarena", "comod"), _a.RolesFor("nerd"));
        Assert.Equal(2, _a.RolesFor("dare").Count);
    }

    [Fact]
    public void the_limit_is_ten_per_account_and_staff_can_raise_it()
    {
        for (var i = 0; i < ArenaStore.MaxArenasPerAccount; i++)
            Make($"arena{(char)('a' + i)}", i % 2 == 0 ? "dare" : "nerd");
        Assert.Equal(10, _a.OwnedCountOf("nerd"));
        Assert.False(_a.Request("nerd", "onemore", "").Ok);
        Assert.True(_a.Request("pal", "palsone", "").Ok); // another account

        Assert.False(_a.RequestMoreArenas("pal", 12, "", out _).Ok); // not at the limit
        Assert.False(_a.RequestMoreArenas("nerd", 10, "", out _).Ok); // not more
        Assert.True(_a.RequestMoreArenas("nerd", 15, "a league", out var id).Ok);
        Assert.False(_a.RequestMoreArenas("dare", 20, "", out _).Ok); // one open ask per account
        Assert.Equal("a league", _a.PendingLimitRequestOf("dare")!.Reason);

        Assert.True(_a.ApproveLimit(id, "chris", 12).Ok);
        Assert.False(_a.ApproveLimit(id, "chris", 13).Ok);
        Assert.Equal(12, _a.ArenaLimitOf("dare"));
        Assert.Null(_a.PendingLimitRequestOf("nerd"));
        Assert.Equal(12, _a.LastLimitAnswerOf("nerd")!.Granted);
        Assert.True(_a.Request("dare", "onemore", "").Ok);
        Assert.True(_a.Approve("onemore", "staff", _ => false).Ok);

        Assert.True(_a.Create("twelve", "staff", "nerd", _ => false, () => null).Ok);
        Assert.False(_a.Create("thirteen", "staff", "nerd", _ => false, () => null).Ok);
        Assert.True(_a.RequestMoreArenas("nerd", 20, "", out var id2).Ok);
        Assert.False(_a.DeclineLimit(id2, "chris", "").Ok);
        Assert.True(_a.DeclineLimit(id2, "chris", "enough for now").Ok);
        Assert.Equal(12, _a.ArenaLimitOf("nerd"));
    }

    [Fact]
    public void a_deleted_name_hands_everything_to_another_name_and_nobody_inherits_it()
    {
        Make("sky", "nerd");
        Make("palsarena", "pal");
        Assert.True(_a.AddCoMod("palsarena", "pal", "nerd").Ok);
        var touched = _a.HandOverName("nerd", "dare", "nerd");
        Assert.True(_s.DeletePilot(_dare.Id, "nerd", out _).Ok);
        Assert.Contains("sky", touched);
        Assert.Equal("dare", _a.Get("sky")!.Owner);
        Assert.Equal(ArenaStatus.Active, _a.Get("sky")!.Status);
        Assert.Equal(new[] { "dare" }, _a.Get("palsarena")!.CoMods);

        Assert.True(_s.CreateWithoutEmail("nerd", Pw, out _).Ok); // someone new claims the name
        Assert.Null(_a.RoleIn("sky", "nerd"));
        Assert.Null(_a.RoleIn("palsarena", "nerd"));
        Assert.Empty(_a.Holdings("nerd").Arenas);
    }
}
