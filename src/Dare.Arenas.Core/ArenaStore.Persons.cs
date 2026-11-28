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
}
