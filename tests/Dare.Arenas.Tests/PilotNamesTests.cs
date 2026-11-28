using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Pilot names on one email: list, add (same password), switch targets, delete; and what a
// deleted name gives up in the arena store; and the staff-name guard.
public sealed class PilotNamesTests : IDisposable
{
    private const string Pw = "hunter22pw";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-pilots-{Guid.NewGuid():N}.db");
    private readonly string _arenaDb = Path.Combine(Path.GetTempPath(), $"dare-pilots-arenas-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountStore _s;
    private readonly ArenaStore _a;

    public PilotNamesTests()
    {
        _s = new AccountStore(_db, () => _now);
        _a = new ArenaStore(_arenaDb, () => _now);
    }

    public void Dispose()
    {
        _s.Dispose();
        _a.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _arenaDb })
            foreach (var x in new[] { f, f + "-wal", f + "-shm", f + "-journal" })
                try { File.Delete(x); } catch { }
    }

    private Account SignUp(string name, string email, string pw = Pw)
    {
        Assert.True(_s.StartSignup(name, pw, email, out var code).Ok);
        Assert.True(_s.VerifySignup(email, code, out var account).Ok);
        return account!;
    }

    [Fact]
    public void names_on_the_same_confirmed_email_are_listed_and_switchable()
    {
        var dare = SignUp("dare", "dare@example.com");
        var nerd = SignUp("nerd", "DARE@example.com");
        SignUp("stranger", "other@example.com");
        Assert.Equal(new[] { "dare", "nerd" }, _s.PilotsOf(dare.Id).Select(a => a.Name));
        Assert.Equal(nerd.Id, _s.PilotOf(dare.Id, "NERD")!.Id);
        Assert.Null(_s.PilotOf(dare.Id, "stranger"));
    }

    [Fact]
    public void an_added_name_shares_the_email_and_starts_with_the_same_password()
    {
        var dare = SignUp("dare", "dare@example.com");
        var r = _s.AddPilot(dare.Id, "nerd", out var nerd);
        Assert.True(r.Ok, r.Message);
        Assert.True(nerd!.EmailVerified);
        Assert.Equal("dare@example.com", nerd.Email);
        Assert.Equal(LoginResult.Ok, _s.CheckLogin("nerd", Pw, out _));
        Assert.Contains(_s.PilotsOf(dare.Id), a => a.Name == "nerd");

        Assert.False(_s.AddPilot(dare.Id, "Dare", out _).Ok); // taken (any case)
        Assert.False(_s.AddPilot(dare.Id, "x", out _).Ok);    // name rules
    }

    [Fact]
    public void adding_a_name_needs_a_confirmed_email_and_respects_the_limit()
    {
        var dare = SignUp("dare", "dare@example.com");
        using (var c = new SqliteConnection($"Data Source={_db};Pooling=false"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE accounts SET email_verified = 0 WHERE id = {dare.Id}";
            cmd.ExecuteNonQuery();
        }
        Assert.False(_s.AddPilot(dare.Id, "nerd", out _).Ok);
        Assert.Single(_s.PilotsOf(dare.Id));

        var other = SignUp("first", "many@example.com");
        for (var i = 1; i < AccountStore.MaxAccountsPerEmail; i++)
            Assert.True(_s.AddPilot(other.Id, $"pilot{(char)('a' + i)}x", out _).Ok);
        Assert.False(_s.AddPilot(other.Id, "onetoomany", out _).Ok);
    }

    [Fact]
    public void deleting_frees_the_name_but_never_the_last_one()
    {
        var dare = SignUp("dare", "dare@example.com");
        Assert.True(_s.AddPilot(dare.Id, "nerd", out var nerd).Ok);
        var pass = _s.IssueGamePass(nerd!.Id)!;

        Assert.True(_s.DeletePilot(dare.Id, "nerd", out var gone).Ok);
        Assert.Equal(nerd.Id, gone!.Id);
        Assert.Null(_s.Find("nerd"));
        Assert.False(_s.RedeemGamePass("nerd", pass, out _));
        SignUp("nerd", "someone@example.com"); // free for anyone

        Assert.False(_s.DeletePilot(dare.Id, "dare", out _).Ok); // the only name left
        Assert.False(_s.DeletePilot(dare.Id, "nerd", out _).Ok); // someone else's now
    }

    [Fact]
    public void only_global_staff_lines_count_as_staff()
    {
        const string conf = "[(global)]\nBigMod = mod\n[skyrace]\nnerd = arenaowner\n[General]\nx = 1\n";
        Assert.True(StaffConf.IsGlobalStaff(conf, "bigmod"));
        Assert.False(StaffConf.IsGlobalStaff(conf, "nerd"));
        Assert.True(StaffConf.IsGlobalStaff("[skyrace]\n(global):Sys = sysop\n", "sys"));
        Assert.False(StaffConf.IsGlobalStaff(null, "anyone"));
    }
}
