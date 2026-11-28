namespace Dare.Arenas.Core;

/// <summary>
/// Pilot names on one account (one person, <see cref="PersonOf"/>): a signed-in pilot sees every
/// active name of the person, may switch to any of them without a password, add a name (it starts
/// with the current name's email and password and then keeps its own password) and delete one
/// (the name is freed at once; what the account owns stays). The email is the account's: setting
/// it on one name sets it on all.
/// </summary>
public sealed partial class AccountStore
{
    /// <summary>The active names of <paramref name="accountId"/>'s person (itself included), by name.</summary>
    public IReadOnlyList<Account> PilotsOf(long accountId)
    {
        lock (_gate)
        {
            var me = LoadAccount("id = $id", ("$id", accountId));
            if (me is null || me.Status != "active") return Array.Empty<Account>();
            return PersonOfLocked(accountId) is { } person ? NamesOfLocked(person) : new List<Account> { me };
        }
    }

    /// <summary>The name <paramref name="name"/> among <paramref name="accountId"/>'s pilots, or null.</summary>
    public Account? PilotOf(long accountId, string name) =>
        PilotsOf(accountId).FirstOrDefault(a => string.Equals(a.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool HasEmail(Account a) => a.EmailVerified && !string.IsNullOrEmpty(a.Email);

    /// <summary>Every name row (any status) of <paramref name="accountId"/>'s person. Under the lock.</summary>
    private List<long> PersonNamesAll(long accountId)
    {
        var ids = new List<long>();
        if (PersonOfLocked(accountId) is { } person)
        {
            using var cmd = Cmd("SELECT id FROM accounts WHERE person_id = $p", ("$p", person));
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        if (!ids.Contains(accountId)) ids.Add(accountId);
        return ids;
    }

    /// <summary>
    /// Adds the pilot name <paramref name="name"/> to <paramref name="accountId"/>'s names (its
    /// confirmed email, or its no-email group), with the same password. Same name rules and
    /// per-account limit as signing up.
    /// </summary>
    public Result AddPilot(long accountId, string name, out Account? added)
    {
        added = null;
        if (!AccountRules.TryNewName(name, out var n, out var err)) return Fail(err);
        long id = 0;
        var res = Tx(() =>
        {
            var src = LoadCred("id = $id", ("$id", accountId));
            var me = LoadAccount("id = $id", ("$id", accountId));
            if (src is null || me is null || me.Status != "active") return Fail("Sign in again first.");
            var withEmail = HasEmail(me);
            if (!withEmail && me.Email is not null)
                return Fail("Confirm this account's recovery email first; your names share it.");
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE name = $n", ("$n", n)) > 0)
                return Fail($"The name '{n}' is taken.");
            if (BotNameTaken(n)) return Fail(BotNameMessage);
            var person = PersonOfLocked(accountId)!.Value;
            if (NamesOfLocked(person).Count >= MaxAccountsPerEmail)
                return Fail($"You already have {MaxAccountsPerEmail} pilot names.");
            using (var c = Cmd("SELECT password_hash, password_salt, hash_iterations, legacy_hash FROM accounts WHERE id = $id", ("$id", accountId)))
            using (var r = c.ExecuteReader())
            {
                if (!r.Read()) return Fail("Sign in again first.");
                Run("INSERT INTO accounts(name, password_hash, password_salt, hash_iterations, legacy_hash, recovery_email, email_verified, created, status, person_id) "
                    + "VALUES($n,$h,$s,$i,$l,$e,$v,$c,'active',$g)",
                    ("$n", n), ("$h", r.IsDBNull(0) ? DBNull.Value : r.GetString(0)), ("$s", r.IsDBNull(1) ? DBNull.Value : r.GetString(1)),
                    ("$i", r.GetInt64(2)), ("$l", r.IsDBNull(3) ? DBNull.Value : r.GetString(3)),
                    ("$e", withEmail ? me.Email! : DBNull.Value), ("$v", withEmail ? 1 : 0), ("$c", Now()),
                    ("$g", person));
            }
            id = Scalar("SELECT last_insert_rowid()");
            return Ok($"'{n}' is yours. It has the same password as {me.Name}.");
        });
        if (res.Ok) added = Get(id);
        return res;
    }

    /// <summary>
    /// Deletes the pilot name <paramref name="name"/> (one of <paramref name="accountId"/>'s): the
    /// account goes and the name is free for anyone. Refuses the only name on the email. The
    /// caller deals with the name's arenas first.
    /// </summary>
    public Result DeletePilot(long accountId, string name, out Account? deleted)
    {
        deleted = PilotOf(accountId, name);
        if (deleted is null) return Fail($"'{name}' isn't one of your names.");
        if (PilotsOf(accountId).Count < 2) return Fail("That's your only name; you can't delete it.");
        var target = deleted;
        return Tx(() =>
        {
            Run("DELETE FROM game_passes WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM pilot_settings WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM shipyard_slots WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM pilot_graphics WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM armory_slots WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM pilot_graphics_prefs WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM pilot_profiles WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM login_failures WHERE name = $n", ("$n", target.Name));
            Run("DELETE FROM email_codes WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM accounts WHERE id = $id", ("$id", target.Id));
            return Ok($"Deleted '{target.Name}'. The name is free for anyone now.");
        });
    }
}
