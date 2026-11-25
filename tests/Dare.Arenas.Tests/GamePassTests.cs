using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Game passes: the site's Play button signs a pilot in to the zone once, within a minute.
public sealed class GamePassTests : IDisposable
{
    private const string Pw = "hunter22pw";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-gamepass-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountStore _s;
    private readonly Account _pilot;

    public GamePassTests()
    {
        _s = new AccountStore(_db, () => _now);
        _pilot = SignUp("Big Chris", "chris@example.com");
    }

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    private Account SignUp(string name, string email)
    {
        Assert.True(_s.StartSignup(name, Pw, email, out var code).Ok);
        Assert.True(_s.VerifySignup(email, code, out var account).Ok);
        return account!;
    }

    private void Sql(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_db};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void a_pass_signs_in_once_for_its_own_account()
    {
        var pass = _s.IssueGamePass(_pilot.Id)!;
        Assert.True(AccountStore.LooksLikeGamePass(pass));
        Assert.True(pass.Length <= 31); // fits the game's password field

        Assert.True(_s.RedeemGamePass("big chris", pass, out var account)); // name in any case
        Assert.Equal("Big Chris", account!.Name);
        Assert.Equal(_now, account.LastLogin);

        Assert.False(_s.RedeemGamePass("Big Chris", pass, out _)); // used up
    }

    [Fact]
    public void a_pass_expires_after_a_minute()
    {
        var pass = _s.IssueGamePass(_pilot.Id)!;
        _now += AccountStore.GamePassLifetime;
        Assert.False(_s.RedeemGamePass("Big Chris", pass, out var account));
        Assert.Null(account);
    }

    [Fact]
    public void a_pass_never_works_for_another_name()
    {
        var other = SignUp("Somebody", "some@example.com");
        var pass = _s.IssueGamePass(_pilot.Id)!;
        Assert.False(_s.RedeemGamePass(other.Name, pass, out _));
        Assert.False(_s.RedeemGamePass("Nobody", pass, out _));
        Assert.True(_s.RedeemGamePass("Big Chris", pass, out _)); // still the owner's
    }

    [Fact]
    public void a_disabled_account_gets_no_pass_and_cannot_use_an_open_one()
    {
        var pass = _s.IssueGamePass(_pilot.Id)!;
        Sql($"UPDATE accounts SET status = 'disabled' WHERE id = {_pilot.Id}");
        Assert.False(_s.RedeemGamePass("Big Chris", pass, out _));
        Assert.Null(_s.IssueGamePass(_pilot.Id));
        Assert.Null(_s.IssueGamePass(999_999));
    }

    [Fact]
    public void only_the_newest_three_passes_stay_open()
    {
        var passes = Enumerable.Range(0, 4).Select(i =>
        {
            _now += TimeSpan.FromSeconds(1);
            return _s.IssueGamePass(_pilot.Id)!;
        }).ToList();
        Assert.Equal(4, passes.Distinct().Count());
        Assert.False(_s.RedeemGamePass("Big Chris", passes[0], out _)); // the oldest went
        foreach (var p in passes.Skip(1))
            Assert.True(_s.RedeemGamePass("Big Chris", p, out _));
    }

    [Fact]
    public void a_wrong_or_malformed_pass_is_not_a_pass_and_the_password_still_works()
    {
        _s.IssueGamePass(_pilot.Id);
        Assert.False(_s.RedeemGamePass("Big Chris", "gp-" + new string('a', 24), out _));
        Assert.False(_s.RedeemGamePass("Big Chris", "gp-short", out _));
        Assert.False(_s.RedeemGamePass("Big Chris", "GP-" + new string('A', 24), out _));
        Assert.False(_s.RedeemGamePass("Big Chris", Pw, out _));
        Assert.Equal(LoginResult.Ok, _s.CheckLogin("Big Chris", Pw, out _));
    }

    [Fact]
    public void a_password_shaped_like_a_pass_still_signs_in_as_a_password()
    {
        const string passShaped = "gp-abcdefghijklmnopqrstuvwx";
        Assert.True(AccountStore.LooksLikeGamePass(passShaped));
        var odd = SignUpWithPassword("Oddly", "odd@example.com", passShaped);
        Assert.False(_s.RedeemGamePass(odd.Name, passShaped, out _));
        Assert.Equal(LoginResult.Ok, _s.CheckLogin(odd.Name, passShaped, out _));
    }

    private Account SignUpWithPassword(string name, string email, string pw)
    {
        Assert.True(_s.StartSignup(name, pw, email, out var code).Ok);
        Assert.True(_s.VerifySignup(email, code, out var account).Ok);
        return account!;
    }
}
