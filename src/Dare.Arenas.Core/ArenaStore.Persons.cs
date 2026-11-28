namespace Dare.Arenas.Core;

// Libraries and arenas belong to the account (person) behind a pilot name, never to the name
// itself. The register shares its db file with AccountStore, so a name's person is read straight
// from accounts.person_id. Without an account (or before AccountStore has made the column) a name
// stands for itself: rows then match by the owner name, as before.
public sealed partial class ArenaStore
{
    /// <summary>The account libraries in this store: who owns each row (<c>owner</c> stays as the name that made it).</summary>
    internal static readonly string[] LibraryTables = ["pilot_maps", "pilot_tilesets", "pilot_lvz", "pilot_setting_sets"];

    private bool _accountsReady;

    /// <summary>The person behind pilot name <paramref name="name"/>, or null (no such account, or no accounts yet).</summary>
    public long? PersonOfName(string? name)
    {
        lock (_gate)
        {
            if (!_accountsReady)
            {
                if (Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'accounts'") == 0
                    || Scalar("SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'person_id'") == 0)
                    return null;
                _accountsReady = true;
            }
            using var c = Cmd("SELECT person_id FROM accounts WHERE name = $n", ("$n", (name ?? "").Trim()));
            return c.ExecuteScalar() is long p ? p : null;
        }
    }

    /// <summary><see cref="PersonOfName"/> as a query parameter (NULL when none).</summary>
    private object PersonParam(string? name) => (object?)PersonOfName(name) ?? DBNull.Value;

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are names of one account (or the same name).</summary>
    public bool SameAccount(string? a, string? b)
    {
        if (string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return PersonOfName(a) is { } pa && PersonOfName(b) is { } pb && pa == pb;
    }

    /// <summary>Adds <c>person_id</c> to the library tables (AccountStore fills it once for older rows).</summary>
    private void AddLibraryPersonColumns()
    {
        foreach (var t in LibraryTables)
        {
            if (Scalar($"SELECT COUNT(*) FROM pragma_table_info('{t}') WHERE name = 'person_id'") == 0)
                Exec($"ALTER TABLE {t} ADD COLUMN person_id INTEGER NULL;");
            Exec($"CREATE INDEX IF NOT EXISTS ix_{t}_person ON {t}(person_id);");
        }
    }

    /// <summary>
    /// The active names of <paramref name="player"/>'s account, <paramref name="player"/> itself
    /// included (just that name when it has no account). Arena owner, co-mod and invite entries
    /// hold one of these names; any of them counts as the account.
    /// </summary>
    public IReadOnlyList<string> NamesOfAccount(string player)
    {
        lock (_gate)
        {
            var list = new List<string>();
            if (PersonOfName(player) is { } p)
            {
                using var c = Cmd("SELECT name FROM accounts WHERE person_id = $p AND status = 'active' ORDER BY name COLLATE NOCASE", ("$p", p));
                using var r = c.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(0));
            }
            if (!list.Any(n => Same(n, player))) list.Add((player ?? "").Trim());
            return list;
        }
    }

    /// <summary>
    /// <c>column IN (...)</c> over <paramref name="player"/>'s account names, adding the parameters
    /// to <paramref name="ps"/> (prefix <paramref name="tag"/> keeps two lists in one query apart).
    /// </summary>
    private string InAccount(string column, string player, List<(string, object)> ps, string tag = "an")
    {
        var names = NamesOfAccount(player);
        var keys = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            keys.Add($"${tag}{i}");
            ps.Add(($"${tag}{i}", names[i]));
        }
        return $"{column} IN ({string.Join(",", keys)})";
    }

    /// <summary>
    /// Hands everything the pilot name <paramref name="deleted"/> holds in the arena system to
    /// <paramref name="to"/>, another name of the same account: arenas and drafts it shows as owner,
    /// its co-mod places, invites and pending requests. Nothing is archived; the account owns it
    /// all before and after. Call before the name's account row goes, so a freed name is never
    /// anyone's owner, co-mod or invitee. Returns the arenas touched (drafts as <c>#name</c>).
    /// </summary>
    public IReadOnlyList<string> HandOverName(string deleted, string to, string actor)
    {
        var touched = new List<string>();
        Tx(() =>
        {
            if (Same(deleted, to)) return Fail("same name");
            using (var c = Cmd("""
                SELECT name FROM arenas WHERE owner = $d
                UNION SELECT '#' || name FROM drafts WHERE owner = $d
                UNION SELECT arena FROM comods WHERE player = $d
                UNION SELECT '#' || name FROM draft_invites WHERE player = $d
                """, ("$d", deleted)))
            using (var r = c.ExecuteReader())
                while (r.Read()) touched.Add(r.GetString(0));
            Run("UPDATE arenas SET owner = $t WHERE owner = $d", ("$t", to), ("$d", deleted));
            Run("UPDATE drafts SET owner = $t WHERE owner = $d", ("$t", to), ("$d", deleted));
            // a place the other name already has is just dropped
            Run("UPDATE OR IGNORE comods SET player = $t WHERE player = $d", ("$t", to), ("$d", deleted));
            Run("DELETE FROM comods WHERE player = $d", ("$d", deleted));
            Run("UPDATE OR IGNORE draft_invites SET player = $t WHERE player = $d", ("$t", to), ("$d", deleted));
            Run("DELETE FROM draft_invites WHERE player = $d", ("$d", deleted));
            Run("UPDATE requests SET player = $t WHERE player = $d AND status = 'Pending'", ("$t", to), ("$d", deleted));
            var now = Now();
            foreach (var a in touched) AddEvent(now, a, actor, "rename", $"{deleted} -> {to} (name deleted; same account)");
            return Ok("");
        });
        return touched;
    }
}
