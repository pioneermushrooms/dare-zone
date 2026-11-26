using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Names claimed with only a password (no recovery email), their extra names, adding an email
// later, and the names kept for guests.
public sealed class NoEmailAccountTests : IDisposable
{
    private const string Pw = "hunter22pw";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-noemail-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountStore _s;

    public NoEmailAccountTests() => _s = new AccountStore(_db, () => _now);

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var x in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(x); } catch { }
    }

    private Account Claim(string name)
    {
        var r = _s.CreateWithoutEmail(name, Pw, out var a);
        Assert.True(r.Ok, r.Message);
        return a!;
    }

    [Fact]
    public void a_name_claimed_without_email_logs_in_at_once()
    {
        var a = Claim("Nomad");
        Assert.Null(a.Email);
        Assert.False(a.EmailVerified);
        Assert.Equal(LoginResult.Ok, _s.CheckLogin("nomad", Pw, out var back));
        Assert.Equal("Nomad", back!.Name);
        Assert.Equal(LoginResult.BadPassword, _s.CheckLogin("nomad", "wrongpassword", out _));
    }

    [Fact]
    public void a_taken_or_bad_name_or_password_is_refused()
    {
        Claim("Nomad");
        Assert.False(_s.CreateWithoutEmail("NOMAD", Pw, out _).Ok);
        Assert.False(_s.CreateWithoutEmail("x", Pw, out _).Ok);
        Assert.False(_s.CreateWithoutEmail("Fine Name", "short", out _).Ok);
        Assert.True(_s.StartSignup("Nomad", Pw, "n@example.com", out var code).Ok);
        Assert.Equal("", code); // the email sign-up sees it as taken too
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("Guest1234")]
    [InlineData("GUEST7")]
    public void guest_names_cant_be_claimed_any_way(string name)
    {
        Assert.True(AccountRules.IsGuestName(name));
        Assert.False(_s.CreateWithoutEmail(name, Pw, out _).Ok);
        Assert.False(_s.StartSignup(name, Pw, "g@example.com", out _).Ok);
        var a = Claim("Nomad");
        Assert.False(_s.AddPilot(a.Id, name, out _).Ok);
    }

    [Theory]
    [InlineData("Guesty")]
    [InlineData("Guest 1")]
    [InlineData("MyGuest1")]
    public void names_that_only_look_like_guest_are_fine(string name) =>
        Assert.False(AccountRules.IsGuestName(name));

    [Fact]
    public void extra_names_without_email_share_the_account()
    {
        var a = Claim("Nomad");
        var r = _s.AddPilot(a.Id, "Drifter", out var b);
        Assert.True(r.Ok, r.Message);
        Assert.Null(b!.Email);
        Assert.Equal(LoginResult.Ok, _s.CheckLogin("Drifter", Pw, out _));
        Assert.True(_s.AddPilot(b.Id, "Rover", out var c).Ok); // from any name of the group
        var names = new[] { "Drifter", "Nomad", "Rover" };
        Assert.Equal(names, _s.PilotsOf(a.Id).Select(p => p.Name));
        Assert.Equal(names, _s.PilotsOf(c!.Id).Select(p => p.Name));
        Claim("Stranger");
        Assert.DoesNotContain(_s.PilotsOf(a.Id), p => p.Name == "Stranger");
        Assert.Single(_s.PilotsOf(_s.Find("Stranger")!.Id));
    }

    [Fact]
    public void deleting_the_first_name_keeps_the_rest_together()
    {
        var a = Claim("Nomad");
        Assert.True(_s.AddPilot(a.Id, "Drifter", out var b).Ok);
        Assert.True(_s.AddPilot(a.Id, "Rover", out _).Ok);
        Assert.True(_s.DeletePilot(b!.Id, "Nomad", out _).Ok);
        Assert.Equal(new[] { "Drifter", "Rover" }, _s.PilotsOf(b.Id).Select(p => p.Name));
        Assert.True(_s.AddPilot(b.Id, "Scout", out _).Ok);
        Assert.Equal(3, _s.PilotsOf(b.Id).Count);
        Assert.Null(_s.Find("Nomad"));
    }

    [Fact]
    public void no_email_names_are_capped_like_an_email()
    {
        var a = Claim("Pilot0");
        for (var i = 1; i < AccountStore.MaxAccountsPerEmail; i++)
            Assert.True(_s.AddPilot(a.Id, "Pilot" + i, out _).Ok);
        Assert.False(_s.AddPilot(a.Id, "PilotX", out _).Ok);
    }

    [Fact]
    public void adding_an_email_later_moves_every_name_onto_it_and_allows_recovery()
    {
        var a = Claim("Nomad");
        Assert.True(_s.AddPilot(a.Id, "Drifter", out var b).Ok);
        Assert.True(_s.StartAddEmail(b!.Id, "nomad@example.com", out var code).Ok);
        var r = _s.VerifyAddEmail(b.Id, code);
        Assert.True(r.Ok, r.Message);
        foreach (var n in new[] { "Nomad", "Drifter" })
        {
            var p = _s.Find(n)!;
            Assert.True(p.EmailVerified);
            Assert.Equal("nomad@example.com", p.Email);
        }
        Assert.Equal(2, _s.PilotsOf(a.Id).Count);
        Assert.True(_s.StartRecovery("nomad@example.com", out var rc, out _, out var pilots).Ok);
        Assert.Equal(2, pilots);
        Assert.True(_s.CompleteRecovery("nomad@example.com", rc, "brandnewpw", "Nomad", out _).Ok);
        Assert.Equal(LoginResult.Ok, _s.CheckLogin("Nomad", "brandnewpw", out _));
    }

    [Fact]
    public void an_email_too_full_for_the_whole_group_is_refused()
    {
        Assert.True(_s.StartSignup("Owner0", Pw, "full@example.com", out var c0).Ok);
        Assert.True(_s.VerifySignup("full@example.com", c0, out var owner).Ok);
        for (var i = 1; i < AccountStore.MaxAccountsPerEmail - 1; i++)
            Assert.True(_s.AddPilot(owner!.Id, "Owner" + i, out _).Ok); // 9 on the email
        var a = Claim("Nomad");
        Assert.True(_s.AddPilot(a.Id, "Drifter", out _).Ok); // 2 in the group: 11 > 10
        Assert.False(_s.StartAddEmail(a.Id, "full@example.com", out _).Ok);
    }

    [Fact]
    public void an_old_database_gets_the_link_column()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        var old = Path.Combine(Path.GetTempPath(), $"dare-noemail-old-{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={old}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE accounts (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL COLLATE NOCASE,
                  password_hash TEXT NULL, password_salt TEXT NULL, hash_iterations INTEGER NOT NULL DEFAULT 0,
                  recovery_email TEXT NULL COLLATE NOCASE, email_verified INTEGER NOT NULL DEFAULT 0, created TEXT NOT NULL,
                  last_login TEXT NULL, status TEXT NOT NULL DEFAULT 'active', isoj_name TEXT NULL, legacy_hash TEXT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using (var s = new AccountStore(old, () => _now))
        {
            Assert.True(s.CreateWithoutEmail("Nomad", Pw, out var a).Ok);
            Assert.True(s.AddPilot(a!.Id, "Drifter", out _).Ok);
            Assert.Equal(2, s.PilotsOf(a.Id).Count);
        }
        SqliteConnection.ClearAllPools();
        foreach (var x in new[] { old, old + "-wal", old + "-shm" }) try { File.Delete(x); } catch { }
    }
}
