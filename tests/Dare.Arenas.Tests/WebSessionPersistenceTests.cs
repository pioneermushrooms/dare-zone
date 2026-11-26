using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Site sign-ins kept in the account store: they survive a zone restart, slide with use, and
// sign-out / password change / reset still end them.
public sealed class WebSessionPersistenceTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-websessions-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly List<AccountStore> _stores = new();

    private AccountStore Open()
    {
        var s = new AccountStore(_db, () => _now);
        _stores.Add(s);
        return s;
    }

    private WebAuth Auth(AccountStore store) => new(() => _now, store);

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void session_survives_a_restart()
    {
        var first = Open();
        var session = Auth(first).IssueSession("Big Chris", 7);
        first.Dispose();
        _stores.Remove(first);

        // a new module load: new store, new WebAuth
        var again = Auth(Open());
        Assert.Equal(("Big Chris", (long?)7), again.SessionFor(session));
    }

    [Fact]
    public void weblogin_session_survives_a_restart()
    {
        var w = Auth(Open());
        var session = w.Redeem(w.IssueLoginToken("Alice"))!;
        var again = Auth(Open());
        Assert.Equal(("Alice", (long?)null), again.SessionFor(session));
    }

    [Fact]
    public void only_a_hash_of_the_session_is_stored()
    {
        var store = Open();
        var session = Auth(store).IssueSession("Big Chris", 7);
        using var c = new SqliteConnection($"Data Source={_db};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT session_hash FROM web_sessions";
        var stored = (string)cmd.ExecuteScalar()!;
        Assert.NotEqual(session, stored);
        Assert.Equal(WebAuth.HashSession(session), stored);
    }

    [Fact]
    public void renew_slides_the_expiry_at_most_once_a_day()
    {
        var w = Auth(Open());
        var s = w.IssueSession("Big Chris", 7);
        Assert.False(w.Renew(s)); // just issued
        _now = _now.AddHours(23);
        Assert.False(w.Renew(s));
        _now = _now.AddHours(2);
        Assert.True(w.Renew(s));  // a day on: renewed to 30 days from now
        Assert.False(w.Renew(s));
        _now = _now.AddDays(29).AddHours(23);
        Assert.NotNull(w.SessionFor(s)); // past 30 days from sign-in, still within 30 days of the visit
        _now = _now.AddHours(2);
        Assert.Null(w.SessionFor(s));
        Assert.False(w.Renew(s)); // expired sessions don't come back
        Assert.False(w.Renew("unknown"));
    }

    [Fact]
    public void sign_out_and_password_change_end_persisted_sessions()
    {
        var store = Open();
        var w = Auth(store);
        var mine = w.IssueSession("Big Chris", 7);
        var otherBrowser = w.IssueSession("big chris", 7);
        var game = w.Redeem(w.IssueLoginToken("BIG CHRIS"))!;
        var alice = w.IssueSession("Alice", 8);

        w.RevokeAllFor("Big Chris", except: mine); // password change
        var again = Auth(Open());
        Assert.NotNull(again.SessionFor(mine));
        Assert.Null(again.SessionFor(otherBrowser));
        Assert.Null(again.SessionFor(game));
        Assert.NotNull(again.SessionFor(alice));

        again.Revoke(mine); // sign-out
        Assert.Null(Auth(Open()).SessionFor(mine));

        again.RevokeAllFor("Alice"); // reset by email: every session
        Assert.Null(again.SessionFor(alice));
    }

    [Fact]
    public void expired_sessions_are_pruned_on_the_next_sign_in()
    {
        var store = Open();
        var w = Auth(store);
        w.IssueSession("Old", 1);
        _now = _now.AddDays(31);
        w.IssueSession("New", 2);
        using var c = new SqliteConnection($"Data Source={_db};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM web_sessions";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void detaching_the_store_falls_back_to_memory()
    {
        var w = Auth(Open());
        var persisted = w.IssueSession("Big Chris", 7);
        w.Attach(null);
        Assert.Null(w.SessionFor(persisted));
        var mem = w.IssueSession("Alice", 8);
        Assert.NotNull(w.SessionFor(mem));
    }
}
