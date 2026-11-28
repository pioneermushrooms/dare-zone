using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// The person behind pilot names: assigned on sign-up, shared by added names, kept when a name is
// deleted or the email changes, and given to every existing name once by the migration.
public sealed class PersonTests : IDisposable
{
    private const string Pw = "hunter22pw";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-person-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private AccountStore _s;

    public PersonTests() => _s = new AccountStore(_db, () => _now);

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var x in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal", _db + ".before-persons" })
            try { File.Delete(x); } catch { }
    }

    private Account SignUp(string name, string email)
    {
        Assert.True(_s.StartSignup(name, Pw, email, out var code).Ok);
        Assert.True(_s.VerifySignup(email, code, out var a).Ok);
        return a!;
    }

    private Account Claim(string name)
    {
        Assert.True(_s.CreateWithoutEmail(name, Pw, out var a).Ok);
        return a!;
    }

    [Fact]
    public void each_sign_up_is_its_own_person_and_added_names_share_it()
    {
        var dare = SignUp("dare", "dare@example.com");
        var nomad = Claim("nomad");
        Assert.NotNull(_s.PersonOf(dare.Id));
        Assert.NotEqual(_s.PersonOf(dare.Id), _s.PersonOf(nomad.Id));
        Assert.True(_s.AddPilot(dare.Id, "nerd", out var nerd).Ok);
        Assert.True(_s.AddPilot(nomad.Id, "drifter", out var drifter).Ok);
        Assert.Equal(_s.PersonOf(dare.Id), _s.PersonOf(nerd!.Id));
        Assert.Equal(_s.PersonOf(nomad.Id), _s.PersonOfName("DRIFTER"));
        Assert.Equal(new[] { "dare", "nerd" }, _s.NamesOf(_s.PersonOf(dare.Id)!.Value).Select(a => a.Name));
        Assert.Null(_s.PersonOfName("nobody"));
        Assert.Null(_s.PersonOf(9999));
    }

    [Fact]
    public void a_sign_up_on_an_email_that_has_names_joins_them()
    {
        var dare = SignUp("dare", "dare@example.com");
        var again = SignUp("again", "DARE@example.com");
        Assert.Equal(_s.PersonOf(dare.Id), _s.PersonOf(again.Id));
    }

    [Fact]
    public void deleting_a_name_keeps_the_person()
    {
        var dare = SignUp("dare", "dare@example.com");
        var person = _s.PersonOf(dare.Id);
        Assert.True(_s.AddPilot(dare.Id, "nerd", out var nerd).Ok);
        Assert.True(_s.DeletePilot(nerd!.Id, "dare", out _).Ok);
        Assert.Equal(person, _s.PersonOf(nerd.Id));
        Assert.Equal(new[] { "nerd" }, _s.NamesOf(person!.Value).Select(a => a.Name));
    }

    [Fact]
    public void the_email_is_the_accounts_and_changing_it_keeps_the_person()
    {
        var nomad = Claim("nomad");
        Assert.True(_s.AddPilot(nomad.Id, "drifter", out var drifter).Ok);
        var person = _s.PersonOf(nomad.Id);
        Assert.True(_s.StartAddEmail(drifter!.Id, "nomad@example.com", out var code).Ok);
        Assert.True(_s.VerifyAddEmail(drifter.Id, code).Ok);
        Assert.Equal("nomad@example.com", _s.Get(nomad.Id)!.Email);
        Assert.True(_s.StartAddEmail(nomad.Id, "new@example.com", out code).Ok);
        Assert.True(_s.VerifyAddEmail(nomad.Id, code).Ok);
        Assert.Equal("new@example.com", _s.Get(drifter.Id)!.Email);
        Assert.Equal(person, _s.PersonOf(drifter.Id));
        Assert.Equal(2, _s.PilotsOf(nomad.Id).Count);
    }

    [Fact]
    public void confirming_an_email_another_account_uses_joins_the_two_accounts()
    {
        var dare = SignUp("dare", "dare@example.com");
        var nomad = Claim("nomad");
        Assert.True(_s.AddPilot(nomad.Id, "drifter", out _).Ok);
        Assert.True(_s.StartAddEmail(nomad.Id, "dare@example.com", out var code).Ok);
        Assert.True(_s.VerifyAddEmail(nomad.Id, code).Ok);
        Assert.Equal(_s.PersonOf(dare.Id), _s.PersonOfName("drifter"));
        Assert.Equal(new[] { "dare", "drifter", "nomad" }, _s.PilotsOf(nomad.Id).Select(a => a.Name));
    }

    [Fact]
    public void the_migration_gives_todays_groups_one_person_each_and_backs_up_first()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
        using (var c = new SqliteConnection($"Data Source={_db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE accounts (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL COLLATE NOCASE,
                  password_hash TEXT NULL, password_salt TEXT NULL, hash_iterations INTEGER NOT NULL DEFAULT 0,
                  recovery_email TEXT NULL COLLATE NOCASE, email_verified INTEGER NOT NULL DEFAULT 0, created TEXT NOT NULL,
                  last_login TEXT NULL, status TEXT NOT NULL DEFAULT 'active', isoj_name TEXT NULL, legacy_hash TEXT NULL,
                  link_id INTEGER NULL);
                INSERT INTO accounts(id, name, recovery_email, email_verified, created) VALUES
                  (1, 'a1', 'a@x.com', 1, '2026-01-01T00:00:00Z'), (2, 'a2', 'A@x.com', 1, '2026-01-01T00:00:00Z'),
                  (3, 'n1', NULL, 0, '2026-01-01T00:00:00Z'), (4, 'b1', 'b@x.com', 1, '2026-01-01T00:00:00Z'),
                  (6, 'u1', 'u@x.com', 0, '2026-01-01T00:00:00Z');
                INSERT INTO accounts(id, name, recovery_email, email_verified, created, link_id) VALUES (5, 'n2', NULL, 0, '2026-01-01T00:00:00Z', 3);
                INSERT INTO accounts(id, name, created, status) VALUES (7, 'staffname', '2026-01-01T00:00:00Z', 'reserved');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _s = new AccountStore(_db, () => _now);
        Assert.True(File.Exists(_db + ".before-persons"));
        long P(long id) => _s.PersonOf(id)!.Value;
        Assert.Equal(P(1), P(2));
        Assert.Equal(P(3), P(5));
        Assert.Equal(5, new[] { P(1), P(3), P(4), P(6), P(7) }.Distinct().Count());
        Assert.Equal(new[] { "n1", "n2" }, _s.PilotsOf(5).Select(a => a.Name));
        // a second start doesn't regroup or back up again
        var before = P(4);
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        _s = new AccountStore(_db, () => _now);
        Assert.Equal(before, _s.PersonOf(4));
    }
}
