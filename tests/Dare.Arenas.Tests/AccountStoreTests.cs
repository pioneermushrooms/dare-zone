using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

public sealed class AccountStoreTests : IDisposable
{
    private const string Pw = "hunter22pw";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-accounts-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly List<AccountStore> _stores = new();

    private AccountStore Open()
    {
        var s = new AccountStore(_db, () => _now);
        _stores.Add(s);
        return s;
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    private Account SignUp(AccountStore s, string name = "Big Chris", string email = "chris@example.com", string pw = Pw)
    {
        Assert.True(s.StartSignup(name, pw, email, out var code).Ok);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.True(s.VerifySignup(email, code, out var account).Ok);
        return account!;
    }

    private static string WrongCode(string code) => code == "000000" ? "000001" : "000000";

    private void Sql(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_db};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string ScalarText(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_db};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    // ---- sign-up ---------------------------------------------------------

    [Fact]
    public void signup_then_verify_creates_account_and_login_ok()
    {
        var s = Open();
        var a = SignUp(s);
        Assert.Equal("Big Chris", a.Name);
        Assert.Equal("chris@example.com", a.Email);
        Assert.True(a.EmailVerified);
        Assert.Equal(_now, a.Created);
        Assert.Null(a.LastLogin);
        Assert.Null(a.IsojName);

        _now = _now.AddMinutes(1);
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", Pw, out var logged));
        Assert.Equal(a.Id, logged!.Id);
        Assert.Equal(_now, logged.LastLogin);
        Assert.Equal(_now, s.Get(a.Id)!.LastLogin);
    }

    [Fact]
    public void signup_email_is_normalized()
    {
        var s = Open();
        Assert.True(s.StartSignup("Chris", Pw, "  Chris@Example.COM ", out var code).Ok);
        Assert.True(s.VerifySignup("chris@example.com", code, out var a).Ok);
        Assert.Equal("chris@example.com", a!.Email);
    }

    [Fact]
    public void signup_validates_inputs()
    {
        var s = Open();
        Assert.False(s.StartSignup("1bad", Pw, "a@b.com", out var c1).Ok);
        Assert.Equal("", c1);
        Assert.False(s.StartSignup("Good", "short", "a@b.com", out _).Ok);
        Assert.False(s.StartSignup("Good", Pw, "not-an-email", out _).Ok);
    }

    [Fact]
    public void same_account_any_case()
    {
        var s = Open();
        var a = SignUp(s, "Big Chris");
        Assert.Equal(a.Id, s.Find("BIG CHRIS")!.Id);
        Assert.Equal("Big Chris", s.Find("big chris")!.Name);
        Assert.Equal(LoginResult.Ok, s.CheckLogin("bIG cHRIS", Pw, out var logged));
        Assert.Equal("Big Chris", logged!.Name);
    }

    [Fact]
    public void taken_name_or_email_returns_ok_with_empty_code()
    {
        var s = Open();
        SignUp(s, "Big Chris", "chris@example.com");
        var fresh = s.StartSignup("Someone", Pw, "someone@example.com", out _);

        var takenName = s.StartSignup("BIG CHRIS", Pw, "other@example.com", out var c1);
        Assert.True(takenName.Ok);
        Assert.Equal("", c1);
        var takenEmail = s.StartSignup("Other", Pw, "CHRIS@example.com", out var c2);
        Assert.True(takenEmail.Ok);
        Assert.Equal("", c2);
        Assert.Equal(fresh.Message.Replace("someone@example.com", "X"), takenName.Message.Replace("other@example.com", "X"));
    }

    [Fact]
    public void signup_race_first_verified_wins()
    {
        var s1 = Open();
        var s2 = Open();
        Assert.True(s1.StartSignup("Racer", Pw, "one@example.com", out var c1).Ok);
        Assert.True(s2.StartSignup("racer", Pw, "two@example.com", out var c2).Ok);
        Assert.NotEqual("", c1);
        Assert.NotEqual("", c2);
        Assert.True(s1.VerifySignup("one@example.com", c1, out var a).Ok);
        var second = s2.VerifySignup("two@example.com", c2, out var b);
        Assert.False(second.Ok);
        Assert.Contains("name", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("taken", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(b);
        Assert.Equal("Racer", s2.Find("RACER")!.Name);
        Assert.Equal(a!.Id, s2.Find("racer")!.Id);
    }

    [Fact]
    public void newer_signup_replaces_earlier_code()
    {
        var s = Open();
        Assert.True(s.StartSignup("First", Pw, "x@example.com", out var old).Ok);
        Assert.True(s.StartSignup("Second", Pw, "x@example.com", out var newer).Ok);
        if (old != newer) Assert.False(s.VerifySignup("x@example.com", old, out _).Ok);
        Assert.True(s.VerifySignup("x@example.com", newer, out var a).Ok);
        Assert.Equal("Second", a!.Name);
        Assert.Null(s.Find("First"));
    }

    [Fact]
    public void no_plain_code_or_password_stored()
    {
        var s = Open();
        Assert.True(s.StartSignup("Plain", Pw, "plain@example.com", out var code).Ok);
        Assert.Equal("0", ScalarText($"SELECT COUNT(*) FROM email_codes WHERE code_hash = '{code}'"));
        Assert.Equal("64", ScalarText("SELECT length(code_hash) FROM email_codes"));
        Assert.Equal("0", ScalarText($"SELECT COUNT(*) FROM pending_signups WHERE password_hash = '{Pw}'"));
    }

    // ---- codes -----------------------------------------------------------

    [Fact]
    public void code_expires_after_10_minutes()
    {
        var s = Open();
        Assert.True(s.StartSignup("Late", Pw, "late@example.com", out var code).Ok);
        _now = _now.AddMinutes(10).AddSeconds(1);
        Assert.False(s.VerifySignup("late@example.com", code, out _).Ok);
        Assert.Null(s.Find("Late"));
    }

    [Fact]
    public void code_valid_just_before_expiry()
    {
        var s = Open();
        Assert.True(s.StartSignup("OnTime", Pw, "ontime@example.com", out var code).Ok);
        _now = _now.AddMinutes(9).AddSeconds(59);
        Assert.True(s.VerifySignup("ontime@example.com", code, out _).Ok);
    }

    [Fact]
    public void code_is_single_use()
    {
        var s = Open();
        Assert.True(s.StartRecovery("chris@example.com", out var none, out var noName).Ok);
        Assert.Equal("", none);
        Assert.Null(noName);
        SignUp(s);
        Assert.True(s.StartRecovery("chris@example.com", out var code, out var name).Ok);
        Assert.Equal("Big Chris", name);
        Assert.True(s.CompleteRecovery("chris@example.com", code, "newpassword1").Ok);
        Assert.False(s.CompleteRecovery("chris@example.com", code, "newpassword2").Ok);
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", "newpassword1", out _));
    }

    [Fact]
    public void five_wrong_tries_void_the_code()
    {
        var s = Open();
        Assert.True(s.StartSignup("Guesser", Pw, "g@example.com", out var code).Ok);
        for (var i = 0; i < 5; i++)
            Assert.False(s.VerifySignup("g@example.com", WrongCode(code), out _).Ok);
        Assert.False(s.VerifySignup("g@example.com", code, out _).Ok);
        Assert.Null(s.Find("Guesser"));
    }

    [Fact]
    public void four_wrong_tries_then_right_code_works()
    {
        var s = Open();
        Assert.True(s.StartSignup("Guesser", Pw, "g@example.com", out var code).Ok);
        for (var i = 0; i < 4; i++)
            Assert.False(s.VerifySignup("g@example.com", WrongCode(code), out _).Ok);
        Assert.True(s.VerifySignup("g@example.com", code, out _).Ok);
    }

    // ---- game login ------------------------------------------------------

    [Fact]
    public void unknown_name_and_bad_password()
    {
        var s = Open();
        SignUp(s);
        Assert.Equal(LoginResult.UnknownName, s.CheckLogin("Nobody", Pw, out var a1));
        Assert.Null(a1);
        Assert.Equal(LoginResult.BadPassword, s.CheckLogin("Big Chris", "wrongpassword", out var a2));
        Assert.Null(a2);
    }

    [Fact]
    public void disabled_account_refused()
    {
        var s = Open();
        SignUp(s);
        Sql("UPDATE accounts SET status = 'locked'");
        Assert.Equal(LoginResult.Disabled, s.CheckLogin("Big Chris", Pw, out _));
    }

    [Fact]
    public void ten_bad_passwords_lock_out_for_10_minutes()
    {
        var s = Open();
        SignUp(s);
        for (var i = 0; i < 10; i++)
            Assert.Equal(LoginResult.BadPassword, s.CheckLogin("Big Chris", "wrongpassword", out _));
        Assert.Equal(LoginResult.LockedOut, s.CheckLogin("big chris", Pw, out var locked));
        Assert.Null(locked);
        _now = _now.AddMinutes(9).AddSeconds(59);
        Assert.Equal(LoginResult.LockedOut, s.CheckLogin("Big Chris", Pw, out _));
        _now = _now.AddSeconds(2);
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", Pw, out var ok));
        Assert.NotNull(ok);
    }

    [Fact]
    public void bad_passwords_outside_10_minute_window_do_not_lock()
    {
        var s = Open();
        SignUp(s);
        for (var i = 0; i < 9; i++)
            Assert.Equal(LoginResult.BadPassword, s.CheckLogin("Big Chris", "wrongpassword", out _));
        _now = _now.AddMinutes(10).AddSeconds(1);
        Assert.Equal(LoginResult.BadPassword, s.CheckLogin("Big Chris", "wrongpassword", out _));
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", Pw, out _));
    }

    // ---- legacy import ---------------------------------------------------

    [Fact]
    public void legacy_import_login_upgrades_hash()
    {
        var s = Open();
        var n = s.ImportLegacy(new[]
        {
            ("OldTimer", PasswordHasher.LegacyHash("OldTimer", "oldpassword")),
            ("Locked One", "lock"),
            ("Anyone", "any"),
        });
        Assert.Equal(1, n);
        Assert.Null(s.Find("Locked One"));
        Assert.Null(s.Find("Anyone"));
        var a = s.Find("oldtimer")!;
        Assert.Null(a.Email);
        Assert.False(a.EmailVerified);

        Assert.Equal(LoginResult.BadPassword, s.CheckLogin("OldTimer", "wrongpassword", out _));
        Assert.Equal(LoginResult.Ok, s.CheckLogin("OldTimer", "oldpassword", out _));
        Assert.Equal("", ScalarText("SELECT legacy_hash FROM accounts WHERE name = 'OldTimer'"));
        Assert.NotEqual("", ScalarText("SELECT password_hash FROM accounts WHERE name = 'OldTimer'"));
        Assert.Equal(LoginResult.Ok, s.CheckLogin("OldTimer", "oldpassword", out _));
        Assert.Equal(LoginResult.BadPassword, s.CheckLogin("OldTimer", "wrongpassword", out _));
    }

    [Fact]
    public void legacy_import_runs_once_and_skips_existing()
    {
        var s = Open();
        SignUp(s, "Big Chris");
        var users = new[]
        {
            ("big chris", PasswordHasher.LegacyHash("big chris", "legacypass")),
            ("Newbie", PasswordHasher.LegacyHash("Newbie", "legacypass")),
        };
        Assert.Equal(1, s.ImportLegacy(users));
        Assert.Equal(LoginResult.BadPassword, s.CheckLogin("Big Chris", "legacypass", out _));
        Assert.Equal(0, Open().ImportLegacy(new[] { ("Another", PasswordHasher.LegacyHash("Another", "legacypass")) }));
        Assert.Null(s.Find("Another"));
    }

    // ---- recovery, email, password ---------------------------------------

    [Fact]
    public void recovery_changes_password_and_old_fails()
    {
        var s = Open();
        SignUp(s);
        Assert.True(s.StartRecovery("CHRIS@example.com", out var code, out var name).Ok);
        Assert.Equal("Big Chris", name);
        Assert.False(s.CompleteRecovery("chris@example.com", code, "short").Ok);
        Assert.True(s.CompleteRecovery("chris@example.com", code, "brandnewpw").Ok);
        Assert.Equal(LoginResult.BadPassword, s.CheckLogin("Big Chris", Pw, out _));
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", "brandnewpw", out _));
    }

    [Fact]
    public void recovery_unknown_email_looks_the_same()
    {
        var s = Open();
        SignUp(s);
        var known = s.StartRecovery("chris@example.com", out _, out _);
        var unknown = s.StartRecovery("nobody@example.com", out var code, out var name);
        Assert.True(unknown.Ok);
        Assert.Equal("", code);
        Assert.Null(name);
        Assert.Equal(known.Message.Replace("chris@example.com", "X"), unknown.Message.Replace("nobody@example.com", "X"));
    }

    [Fact]
    public void add_email_for_imported_account()
    {
        var s = Open();
        s.ImportLegacy(new[] { ("OldTimer", PasswordHasher.LegacyHash("OldTimer", "oldpassword")) });
        var a = s.Find("OldTimer")!;
        Assert.True(s.StartAddEmail(a.Id, "Old@Example.com", out var code).Ok);
        Assert.False(s.VerifyAddEmail(a.Id, WrongCode(code)).Ok);
        Assert.True(s.VerifyAddEmail(a.Id, code).Ok);
        var after = s.Get(a.Id)!;
        Assert.Equal("old@example.com", after.Email);
        Assert.True(after.EmailVerified);
        Assert.True(s.StartRecovery("old@example.com", out var rc, out var rn).Ok);
        Assert.NotEqual("", rc);
        Assert.Equal("OldTimer", rn);
    }

    [Fact]
    public void add_email_taken_by_other_account_sends_nothing()
    {
        var s = Open();
        SignUp(s, "Big Chris", "chris@example.com");
        s.ImportLegacy(new[] { ("OldTimer", PasswordHasher.LegacyHash("OldTimer", "oldpassword")) });
        var a = s.Find("OldTimer")!;
        Assert.True(s.StartAddEmail(a.Id, "chris@example.com", out var code).Ok);
        Assert.Equal("", code);
    }

    [Fact]
    public void change_password_needs_current()
    {
        var s = Open();
        var a = SignUp(s);
        Assert.False(s.ChangePassword(a.Id, "wrongpassword", "anotherpw1").Ok);
        Assert.False(s.ChangePassword(a.Id, Pw, "short").Ok);
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", Pw, out _));
        Assert.True(s.ChangePassword(a.Id, Pw, "anotherpw1").Ok);
        Assert.Equal(LoginResult.BadPassword, s.CheckLogin("Big Chris", Pw, out _));
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", "anotherpw1", out _));
    }

    [Fact]
    public void shares_db_with_arena_store()
    {
        using var arenas = new ArenaStore(_db, () => _now);
        var s = Open();
        SignUp(s);
        Assert.True(arenas.Request("Big Chris", "myarena", "pitch").Ok);
        Assert.NotNull(s.Find("Big Chris"));
    }
}
