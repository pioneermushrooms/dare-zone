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
        Assert.Equal(3, n);
        Assert.Equal("locked", s.Find("Locked One")!.Status);
        Assert.Equal("reserved", s.Find("Anyone")!.Status);
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

    // ---- cancelling a code whose email could not be sent ------------------

    [Fact]
    public void send_failure_creates_nothing()
    {
        var s = Open();
        Assert.True(s.StartSignup("Big Chris", Pw, "chris@example.com", out var code).Ok);
        Assert.NotEqual("", code);
        s.CancelPending(CodePurpose.Signup, "Chris@Example.com "); // the send failed
        Assert.False(s.VerifySignup("chris@example.com", code, out var none).Ok);
        Assert.Null(none);
        Assert.Null(s.Find("Big Chris"));
        Assert.Equal("0", ScalarText("SELECT COUNT(*) FROM pending_signups"));
        Assert.Equal("0", ScalarText("SELECT COUNT(*) FROM email_codes WHERE used = 0"));
        // the name is not reserved: someone else can claim it
        var other = SignUp(s, "Big Chris", "other@example.com");
        Assert.Equal("Big Chris", other.Name);
        // and the first email can still sign up later
        SignUp(s, "Second Name", "chris@example.com");
    }

    [Fact]
    public void cancelled_recovery_code_is_void()
    {
        var s = Open();
        SignUp(s);
        Assert.True(s.StartRecovery("chris@example.com", out var code, out _).Ok);
        s.CancelPending(CodePurpose.Recovery, "chris@example.com");
        Assert.False(s.CompleteRecovery("chris@example.com", code, "brandnewpw1").Ok);
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", Pw, out _));
    }

    [Fact]
    public void cancelled_add_email_code_is_void_and_only_that_accounts()
    {
        var s = Open();
        s.ImportLegacy(new[] { ("OldTimer", PasswordHasher.LegacyHash("OldTimer", "oldpassword")),
                               ("Another", PasswordHasher.LegacyHash("Another", "oldpassword")) });
        var a = s.Find("OldTimer")!;
        var b = s.Find("Another")!;
        Assert.True(s.StartAddEmail(a.Id, "old@example.com", out var codeA).Ok);
        Assert.True(s.StartAddEmail(b.Id, "b@example.com", out var codeB).Ok);
        s.CancelPending(CodePurpose.AddEmail, "old@example.com", a.Id);
        Assert.False(s.VerifyAddEmail(a.Id, codeA).Ok);
        Assert.False(s.Get(a.Id)!.EmailVerified);
        Assert.True(s.VerifyAddEmail(b.Id, codeB).Ok);
    }

    [Fact]
    public void cancel_with_nothing_pending_is_harmless()
    {
        var s = Open();
        s.CancelPending(CodePurpose.Signup, "nobody@example.com");
        s.CancelPending(CodePurpose.Recovery, "not an email");
        SignUp(s);
    }

    [Fact]
    public void find_by_email_is_normalized_and_null_when_unknown()
    {
        var s = Open();
        var a = SignUp(s);
        Assert.Equal(a.Id, s.FindByEmail(" Chris@Example.COM ")!.Id);
        Assert.Null(s.FindByEmail("nobody@example.com"));
        Assert.Null(s.FindByEmail("not an email"));
    }

    // ---- final fix wave ----------------------------------------------------

    // I2(b): reset verifies the code BEFORE hashing the new password
    [Fact]
    public void reset_with_a_wrong_code_does_not_run_pbkdf2()
    {
        var s = Open();
        SignUp(s);
        Assert.True(s.StartRecovery("chris@example.com", out var code, out _).Ok);
        var before = PasswordHasher.ThreadHashCalls;
        Assert.False(s.CompleteRecovery("chris@example.com", WrongCode(code), "newpassword1").Ok);
        Assert.False(s.CompleteRecovery("nobody@example.com", "123456", "newpassword1").Ok);
        Assert.Equal(before, PasswordHasher.ThreadHashCalls);
        Assert.True(s.CompleteRecovery("chris@example.com", code, "newpassword1").Ok);
        Assert.Equal(before + 1, PasswordHasher.ThreadHashCalls);
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", "newpassword1", out _));
    }

    // I2(d): web wrong passwords never count toward (or honour) the game lockout
    [Fact]
    public void web_login_failures_do_not_lock_the_game_login()
    {
        var s = Open();
        SignUp(s);
        for (var i = 0; i < 15; i++)
            Assert.Equal(LoginResult.BadPassword, s.CheckWebLogin("Big Chris", "wrongpassword", out _));
        Assert.Equal("0", ScalarText("SELECT COUNT(*) FROM login_failures"));
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", Pw, out _));
    }

    [Fact]
    public void web_login_works_while_the_game_name_is_locked()
    {
        var s = Open();
        SignUp(s);
        for (var i = 0; i < 10; i++) s.CheckLogin("Big Chris", "wrongpassword", out _);
        Assert.Equal(LoginResult.LockedOut, s.CheckLogin("Big Chris", Pw, out _));
        Assert.Equal(LoginResult.Ok, s.CheckWebLogin("Big Chris", Pw, out var a));
        Assert.Equal("Big Chris", a!.Name);
        Assert.Equal(LoginResult.LockedOut, s.CheckLogin("Big Chris", Pw, out _));
    }

    [Fact]
    public void web_login_unknown_and_reserved_names()
    {
        var s = Open();
        s.ImportLegacy(new[] { ("Locked One", "lock") });
        Assert.Equal(LoginResult.UnknownName, s.CheckWebLogin("Nobody", "whatever1", out _));
        Assert.Equal(LoginResult.Disabled, s.CheckWebLogin("Locked One", "whatever1", out _));
    }

    // I3: cumulative wrong-code cap per (email, purpose) per 24 h, across replaced codes
    [Fact]
    public void twenty_wrong_recovery_codes_in_a_day_block_even_the_right_code()
    {
        var s = Open();
        SignUp(s);
        for (var round = 0; round < 4; round++)
        {
            Assert.True(s.StartRecovery("chris@example.com", out var c, out _).Ok);
            for (var t = 0; t < 5; t++)
                Assert.False(s.CompleteRecovery("chris@example.com", WrongCode(c), "newpassword1").Ok);
            _now = _now.AddMinutes(1);
        }
        Assert.True(s.StartRecovery("chris@example.com", out var right, out _).Ok);
        Assert.NotEqual("", right);
        var r = s.CompleteRecovery("chris@example.com", right, "newpassword1");
        Assert.False(r.Ok);
        Assert.Equal("That code is wrong or has expired. Request a new one.", r.Message); // same generic message
        Assert.Equal(LoginResult.Ok, s.CheckLogin("Big Chris", Pw, out _)); // password untouched

        _now = _now.AddHours(24).AddMinutes(1);
        Assert.True(s.StartRecovery("chris@example.com", out var later, out _).Ok);
        Assert.True(s.CompleteRecovery("chris@example.com", later, "newpassword1").Ok);
    }

    [Fact]
    public void nineteen_wrong_codes_do_not_block()
    {
        var s = Open();
        SignUp(s);
        string c = "";
        for (var round = 0; round < 4; round++)
        {
            Assert.True(s.StartRecovery("chris@example.com", out c, out _).Ok);
            for (var t = 0; t < (round == 3 ? 4 : 5); t++)
                Assert.False(s.CompleteRecovery("chris@example.com", WrongCode(c), "newpassword1").Ok);
        }
        Assert.True(s.CompleteRecovery("chris@example.com", c, "newpassword1").Ok);
    }

    [Fact]
    public void the_cap_is_per_purpose_and_per_email()
    {
        var s = Open();
        SignUp(s);
        SignUp(s, "Other One", "other@example.com");
        for (var round = 0; round < 4; round++)
        {
            Assert.True(s.StartRecovery("chris@example.com", out var c, out _).Ok);
            for (var t = 0; t < 5; t++) s.CompleteRecovery("chris@example.com", WrongCode(c), "newpassword1");
        }
        Assert.True(s.StartRecovery("other@example.com", out var oc, out _).Ok);
        Assert.True(s.CompleteRecovery("other@example.com", oc, "newpassword1").Ok);
        // a sign-up code for the same (blocked) email is a different purpose
        Assert.True(s.StartSignup("Third", Pw, "chris@example.com", out var sc).Ok);
        Assert.Equal("", sc); // email taken: nothing sent
    }

    [Fact]
    public void the_cap_also_covers_signup_and_add_email_codes()
    {
        var s = Open();
        for (var round = 0; round < 4; round++)
        {
            Assert.True(s.StartSignup("Newbie", Pw, "new@example.com", out var c).Ok);
            for (var t = 0; t < 5; t++) s.VerifySignup("new@example.com", WrongCode(c), out _);
        }
        Assert.True(s.StartSignup("Newbie", Pw, "new@example.com", out var right).Ok);
        Assert.False(s.VerifySignup("new@example.com", right, out _).Ok);
        Assert.Null(s.Find("Newbie"));
    }

    // I4: reserved / locked names
    [Fact]
    public void staff_names_without_a_hash_are_imported_as_reserved_and_cannot_be_signed_up()
    {
        var s = Open();
        var n = s.ImportLegacy(
            new[] { ("dare", PasswordHasher.LegacyHash("dare", "darepassword")) },
            new[] { "dare", "Moddy", "helper" });
        Assert.Equal(3, n);
        Assert.Equal("active", s.Find("dare")!.Status);
        Assert.Equal("reserved", s.Find("moddy")!.Status);
        Assert.Equal("reserved", s.Find("Helper")!.Status);

        Assert.True(s.StartSignup("Moddy", Pw, "moddy@example.com", out var code).Ok);
        Assert.Equal("", code); // nothing sent
        Assert.Equal(LoginResult.Disabled, s.CheckLogin("Moddy", Pw, out _));
        Assert.Equal(LoginResult.Disabled, s.CheckWebLogin("Moddy", Pw, out _));
        Assert.Equal(LoginResult.Ok, s.CheckLogin("dare", "darepassword", out _));
    }

    [Fact]
    public void lock_entries_import_as_locked_and_any_as_reserved_even_for_staff()
    {
        var s = Open();
        var n = s.ImportLegacy(
            new[] { ("Barred", "lock"), ("Open Door", "any"), ("Staffer", "any") },
            new[] { "Staffer", "Barred" });
        Assert.Equal(3, n);
        Assert.Equal("locked", s.Find("Barred")!.Status);
        Assert.Equal("reserved", s.Find("Open Door")!.Status);
        Assert.Equal("reserved", s.Find("Staffer")!.Status);
        foreach (var name in new[] { "Barred", "Open Door", "Staffer" })
        {
            Assert.Equal("", SignupCode(s, name));
            Assert.Equal(LoginResult.Disabled, s.CheckLogin(name, "anything1", out _));
        }
    }

    private static string SignupCode(AccountStore s, string name)
    {
        Assert.True(s.StartSignup(name, Pw, name.Replace(" ", "") + "@example.com", out var code).Ok);
        return code;
    }

    [Fact]
    public void staff_names_that_already_have_an_account_are_left_alone()
    {
        var s = Open();
        SignUp(s, "Moddy", "moddy@example.com");
        Assert.Equal(0, s.ImportLegacy(Array.Empty<(string, string)>(), new[] { "moddy" }));
        Assert.Equal("active", s.Find("Moddy")!.Status);
    }

    // minor: legacy password longer than 31 characters
    [Fact]
    public void legacy_login_with_a_long_password_rehashes_the_first_31_characters()
    {
        var s = Open();
        var longPw = "abcdefghijklmnopqrstuvwxyz0123456789"; // 36 chars
        var first31 = longPw[..31];
        s.ImportLegacy(new[] { ("OldTimer", PasswordHasher.LegacyHash("OldTimer", first31)) });
        Assert.Equal(LoginResult.Ok, s.CheckWebLogin("OldTimer", longPw, out _));
        // the game client sends at most 31 characters and must now get in
        Assert.Equal(LoginResult.Ok, s.CheckLogin("OldTimer", first31, out _));
    }
}
