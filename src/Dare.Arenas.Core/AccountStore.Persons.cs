namespace Dare.Arenas.Core;

/// <summary>
/// The person behind pilot names: every name row has a <c>person_id</c> (table <c>people</c>).
/// Saved libraries, arenas and editing rights belong to the person, so switching names, adding
/// or deleting one, or changing the email never changes what the pilot owns. A new sign-up is a
/// new person; an added name joins its person; a deleted name leaves the person and everything
/// it owns in place (the last name can't be deleted). Names on one email are always one person.
/// </summary>
public sealed partial class AccountStore
{
    private const string PersonsMarker = "persons_v1";

    /// <summary>The person the name row <paramref name="accountId"/> belongs to; null when there is no such row.</summary>
    public long? PersonOf(long accountId)
    {
        lock (_gate) return PersonOfLocked(accountId);
    }

    /// <summary>The person the pilot name <paramref name="name"/> belongs to (case-insensitive); null when unknown.</summary>
    public long? PersonOfName(string name)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT id FROM accounts WHERE name = $n", ("$n", (name ?? "").Trim()));
            return c.ExecuteScalar() is long id ? PersonOfLocked(id) : null;
        }
    }

    /// <summary>The person's active names, by name.</summary>
    public IReadOnlyList<Account> NamesOf(long person)
    {
        lock (_gate) return NamesOfLocked(person);
    }

    private List<Account> NamesOfLocked(long person)
    {
        var ids = new List<long>();
        using (var c = Cmd("SELECT id FROM accounts WHERE person_id = $p AND status = 'active' ORDER BY name COLLATE NOCASE", ("$p", person)))
        using (var r = c.ExecuteReader())
            while (r.Read()) ids.Add(r.GetInt64(0));
        return ids.Select(id => LoadAccount("id = $id", ("$id", id))).OfType<Account>().ToList();
    }

    /// <summary>Under the lock. A row without a person (made by older code) gets a new one here.</summary>
    private long? PersonOfLocked(long accountId)
    {
        using (var c = Cmd("SELECT person_id FROM accounts WHERE id = $id", ("$id", accountId)))
        {
            var v = c.ExecuteScalar();
            if (v is null) return null;
            if (v is long p) return p;
        }
        var person = NewPerson();
        Run("UPDATE accounts SET person_id = $p WHERE id = $id", ("$p", person), ("$id", accountId));
        return person;
    }

    /// <summary>A new row in <c>people</c>. Under the lock.</summary>
    private long NewPerson()
    {
        Run("INSERT INTO people(created) VALUES($c)", ("$c", Now()));
        return Scalar("SELECT last_insert_rowid()");
    }

    /// <summary>The person to give a new name with <paramref name="email"/> confirmed: the email's names' person, or a new one. Under the lock.</summary>
    private long PersonForEmail(string? email)
    {
        if (!string.IsNullOrEmpty(email))
        {
            using var c = Cmd("SELECT id FROM accounts WHERE recovery_email = $e AND email_verified = 1 ORDER BY id LIMIT 1", ("$e", email));
            if (c.ExecuteScalar() is long id && PersonOfLocked(id) is { } p) return p;
        }
        return NewPerson();
    }

    /// <summary>
    /// Creates <c>people</c> and <c>accounts.person_id</c>, and once (marker <c>persons_v1</c>)
    /// gives every existing name its person: names on one confirmed email share one, names
    /// without an email share one per <c>link_id</c> group, every other row gets its own. The
    /// db is copied to <paramref name="backupPath"/> first when there are accounts to move.
    /// </summary>
    private void EnsurePeople(string? backupPath)
    {
        Exec("CREATE TABLE IF NOT EXISTS people (id INTEGER PRIMARY KEY AUTOINCREMENT, created TEXT NOT NULL);");
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'person_id'") == 0)
            Exec("ALTER TABLE accounts ADD COLUMN person_id INTEGER NULL;");
        Exec("CREATE INDEX IF NOT EXISTS ix_accounts_person ON accounts(person_id);");
        if (Scalar("SELECT COUNT(*) FROM account_meta WHERE key = $k", ("$k", PersonsMarker)) > 0) return;

        if (backupPath is not null && Scalar("SELECT COUNT(*) FROM accounts") > 0 && !File.Exists(backupPath))
            Run("VACUUM INTO $p", ("$p", backupPath));

        Tx(() =>
        {
            var rows = new List<(long Id, string? Email, bool Verified, long? Link, string Status)>();
            using (var c = Cmd("SELECT id, recovery_email, email_verified, link_id, status FROM accounts WHERE person_id IS NULL ORDER BY id"))
            using (var r = c.ExecuteReader())
                while (r.Read())
                    rows.Add((r.GetInt64(0), Str(r, 1), r.GetInt64(2) != 0, r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4)));
            var byEmail = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var byGroup = new Dictionary<long, long>();
            foreach (var row in rows)
            {
                long person;
                if (row.Status != "active")
                    person = NewPerson();
                else if (row.Verified && !string.IsNullOrEmpty(row.Email))
                {
                    if (!byEmail.TryGetValue(row.Email!, out person)) byEmail[row.Email!] = person = NewPerson();
                }
                else if (row.Email is null)
                {
                    var group = row.Link ?? row.Id;
                    if (!byGroup.TryGetValue(group, out person)) byGroup[group] = person = NewPerson();
                }
                else
                    person = NewPerson(); // unconfirmed email: on its own, as before
                Run("UPDATE accounts SET person_id = $p WHERE id = $id", ("$p", person), ("$id", row.Id));
            }
            Run("INSERT INTO account_meta(key, value) VALUES($k,$v)", ("$k", PersonsMarker), ("$v", $"{Now()} {rows.Count}"));
            return Ok("");
        });
    }

    /// <summary>
    /// Makes person <paramref name="from"/> part of <paramref name="into"/>: its names and every
    /// row in any table with a <c>person_id</c> column (libraries, arenas, limits, in this db file)
    /// move over. A one-per-person row that <paramref name="into"/> already has is kept as
    /// <paramref name="into"/>'s. Under the lock, inside a transaction.
    /// </summary>
    private void JoinPersons(long from, long into)
    {
        var tables = new List<string>();
        using (var c = Cmd("SELECT m.name FROM sqlite_master m WHERE m.type = 'table' AND m.name NOT IN ('people', 'accounts') "
            + "AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) p WHERE p.name = 'person_id')"))
        using (var r = c.ExecuteReader())
            while (r.Read()) tables.Add(r.GetString(0));
        foreach (var t in tables)
        {
            Run($"UPDATE OR IGNORE \"{t}\" SET person_id = $to WHERE person_id = $from", ("$to", into), ("$from", from));
            Run($"DELETE FROM \"{t}\" WHERE person_id = $from", ("$from", from));
        }
        Run("UPDATE accounts SET person_id = $to WHERE person_id = $from", ("$to", into), ("$from", from));
    }
}
