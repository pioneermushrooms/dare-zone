namespace Dare.Arenas.Core;

/// <summary>
/// Pilot names on one email: a signed-in pilot sees every active name confirmed on the same
/// recovery email, may switch to any of them without a password (same email, same person),
/// add a name (it starts with the current name's password and then keeps its own) and delete
/// one (the name is freed at once). An account without a confirmed email has only itself.
/// </summary>
public sealed partial class AccountStore
{
    /// <summary>Active names sharing <paramref name="accountId"/>'s confirmed email (itself included), by name.</summary>
    public IReadOnlyList<Account> PilotsOf(long accountId)
    {
        lock (_gate)
        {
            var me = LoadAccount("id = $id", ("$id", accountId));
            if (me is null || me.Status != "active") return Array.Empty<Account>();
            if (!me.EmailVerified || string.IsNullOrEmpty(me.Email)) return new[] { me };
            using var cmd = Cmd("SELECT id FROM accounts WHERE recovery_email = $e AND email_verified = 1 AND status = 'active' ORDER BY name COLLATE NOCASE",
                ("$e", me.Email));
            var ids = new List<long>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) ids.Add(r.GetInt64(0));
            return ids.Select(id => LoadAccount("id = $id", ("$id", id))).OfType<Account>().ToList();
        }
    }

    /// <summary>The name <paramref name="name"/> among <paramref name="accountId"/>'s pilots, or null.</summary>
    public Account? PilotOf(long accountId, string name) =>
        PilotsOf(accountId).FirstOrDefault(a => string.Equals(a.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds the pilot name <paramref name="name"/> to <paramref name="accountId"/>'s confirmed email,
    /// with the same password. Same name rules and per-email limit as signing up.
    /// </summary>
    public Result AddPilot(long accountId, string name, out Account? added)
    {
        added = null;
        if (!AccountRules.TryName(name, out var n, out var err)) return Fail(err);
        long id = 0;
        var res = Tx(() =>
        {
            var src = LoadCred("id = $id", ("$id", accountId));
            var me = LoadAccount("id = $id", ("$id", accountId));
            if (src is null || me is null || me.Status != "active") return Fail("Sign in again first.");
            if (!me.EmailVerified || string.IsNullOrEmpty(me.Email))
                return Fail("Confirm a recovery email on this account first; your names share it.");
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE name = $n", ("$n", n)) > 0)
                return Fail($"The name '{n}' is taken.");
            if (PilotsOn(me.Email) >= MaxAccountsPerEmail) return Fail(TooManyPilots);
            using (var c = Cmd("SELECT password_hash, password_salt, hash_iterations, legacy_hash FROM accounts WHERE id = $id", ("$id", accountId)))
            using (var r = c.ExecuteReader())
            {
                if (!r.Read()) return Fail("Sign in again first.");
                Run("INSERT INTO accounts(name, password_hash, password_salt, hash_iterations, legacy_hash, recovery_email, email_verified, created, status) "
                    + "VALUES($n,$h,$s,$i,$l,$e,1,$c,'active')",
                    ("$n", n), ("$h", r.IsDBNull(0) ? DBNull.Value : r.GetString(0)), ("$s", r.IsDBNull(1) ? DBNull.Value : r.GetString(1)),
                    ("$i", r.GetInt64(2)), ("$l", r.IsDBNull(3) ? DBNull.Value : r.GetString(3)), ("$e", me.Email), ("$c", Now()));
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
            Run("DELETE FROM login_failures WHERE name = $n", ("$n", target.Name));
            Run("DELETE FROM email_codes WHERE account_id = $id", ("$id", target.Id));
            Run("DELETE FROM accounts WHERE id = $id", ("$id", target.Id));
            return Ok($"Deleted '{target.Name}'. The name is free for anyone now.");
        });
    }
}
