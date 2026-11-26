namespace Dare.Arenas.Core;

/// <summary>
/// Site sessions (<see cref="WebAuth"/>) kept in the database, so pilots stay signed in across
/// zone restarts. Only SHA-256 hashes of the session ids are stored.
/// </summary>
public sealed partial class AccountStore : IWebSessionStore
{
    private void CreateWebSessionTable() => Exec("""
        CREATE TABLE IF NOT EXISTS web_sessions (
          session_hash TEXT NOT NULL PRIMARY KEY,
          player TEXT NOT NULL COLLATE NOCASE,
          account_id INTEGER NULL,
          created TEXT NOT NULL,
          expires TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_web_sessions_player ON web_sessions(player COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_web_sessions_expires ON web_sessions(expires);
        """);

    public void SaveSession(string hash, string player, long? accountId, DateTimeOffset expires)
    {
        lock (_gate)
            Run("INSERT OR REPLACE INTO web_sessions(session_hash, player, account_id, created, expires) VALUES($h, $p, $a, $c, $e)",
                ("$h", hash), ("$p", player), ("$a", (object?)accountId ?? DBNull.Value), ("$c", Now()), ("$e", Fmt(expires)));
    }

    public (string Player, long? AccountId, DateTimeOffset Expires)? FindSession(string hash, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT player, account_id, expires FROM web_sessions WHERE session_hash = $h AND expires > $now",
                ("$h", hash), ("$now", Fmt(now)));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return (r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), Parse(r.GetString(2)));
        }
    }

    public void ExtendSession(string hash, DateTimeOffset expires)
    {
        lock (_gate) Run("UPDATE web_sessions SET expires = $e WHERE session_hash = $h", ("$e", Fmt(expires)), ("$h", hash));
    }

    public void DeleteSession(string hash)
    {
        lock (_gate) Run("DELETE FROM web_sessions WHERE session_hash = $h", ("$h", hash));
    }

    public void DeleteSessionsFor(string player, string? exceptHash)
    {
        lock (_gate)
            Run("DELETE FROM web_sessions WHERE player = $p AND session_hash <> $x", ("$p", player ?? ""), ("$x", exceptHash ?? ""));
    }

    public void PruneSessions(DateTimeOffset now)
    {
        lock (_gate) Run("DELETE FROM web_sessions WHERE expires <= $now", ("$now", Fmt(now)));
    }
}
