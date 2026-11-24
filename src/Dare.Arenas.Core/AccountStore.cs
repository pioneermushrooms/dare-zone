using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Core;

/// <summary>
/// SQLite-backed store for Foundation accounts: sign-up via emailed code, game login (PBKDF2 or
/// imported legacy hash, with lockout), recovery, adding an email, changing the password, and the
/// one-time passwd.conf import. Shares the db file with <see cref="ArenaStore"/> (own connection,
/// own tables). Codes are returned only so the caller can email them; the store keeps SHA-256
/// hashes of codes and PBKDF2 hashes of passwords, never the plain values. PBKDF2 work runs
/// outside the store's lock; mutations run in one BEGIN IMMEDIATE transaction each.
/// Send-rate limits are the caller's job; the store enforces code expiry, single use, the
/// 5-wrong-tries rule and the game-login lockout.
/// </summary>
public sealed class AccountStore : IDisposable
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);
    public const int MaxCodeAttempts = 5;
    public const int MaxBadLogins = 10;

    private const string LegacyImportMarker = "legacy_import";
    private const string BadCode = "That code is wrong or has expired. Request a new one.";

    private readonly SqliteConnection _conn;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public AccountStore(string dbPath, Func<DateTimeOffset> clock)
    {
        _clock = clock;
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        Exec("PRAGMA busy_timeout = 30000;");
        Exec("PRAGMA journal_mode = WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS accounts (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL COLLATE NOCASE,
              password_hash TEXT NULL,
              password_salt TEXT NULL,
              hash_iterations INTEGER NOT NULL DEFAULT 0,
              recovery_email TEXT NULL COLLATE NOCASE,
              email_verified INTEGER NOT NULL DEFAULT 0,
              created TEXT NOT NULL,
              last_login TEXT NULL,
              status TEXT NOT NULL DEFAULT 'active',
              isoj_name TEXT NULL,
              legacy_hash TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_name ON accounts(name COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_email ON accounts(recovery_email COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS email_codes (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              purpose TEXT NOT NULL,
              email TEXT NOT NULL COLLATE NOCASE,
              account_id INTEGER NULL,
              code_hash TEXT NOT NULL,
              expires TEXT NOT NULL,
              attempts INTEGER NOT NULL DEFAULT 0,
              used INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_email_codes_email ON email_codes(purpose, email);
            CREATE INDEX IF NOT EXISTS ix_email_codes_account ON email_codes(purpose, account_id);
            CREATE TABLE IF NOT EXISTS pending_signups (
              email TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
              name TEXT NOT NULL COLLATE NOCASE,
              password_hash TEXT NOT NULL,
              password_salt TEXT NOT NULL,
              hash_iterations INTEGER NOT NULL,
              expires TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS login_failures (
              name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
              window_start TEXT NOT NULL,
              count INTEGER NOT NULL,
              locked_until TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS account_meta (
              key TEXT NOT NULL PRIMARY KEY,
              value TEXT NOT NULL
            );
            """);
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }

    // ---- sign-up ---------------------------------------------------------

    /// <summary>
    /// Starts a sign-up. When the name or email already belongs to an account, returns the same Ok
    /// result with an empty <paramref name="code"/> (the caller sends nothing). A newer sign-up for
    /// the same email replaces the earlier pending one.
    /// </summary>
    public Result StartSignup(string name, string password, string email, out string code)
    {
        code = "";
        if (!AccountRules.TryName(name, out var n, out var err)) return Fail(err);
        if (!AccountRules.TryPassword(password, out err)) return Fail(err);
        if (!AccountRules.TryEmail(email, out var e, out err)) return Fail(err);
        var (hash, salt, iter) = PasswordHasher.Hash(password); // slow; outside the lock
        var sent = SentMessage(e);
        string issued = "";
        var res = Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE name = $n OR recovery_email = $e", ("$n", n), ("$e", e)) > 0)
                return sent;
            var expires = Fmt(_clock() + CodeLifetime);
            Run("INSERT OR REPLACE INTO pending_signups(email, name, password_hash, password_salt, hash_iterations, expires) "
                + "VALUES($e,$n,$h,$s,$i,$x)", ("$e", e), ("$n", n), ("$h", hash), ("$s", salt), ("$i", iter), ("$x", expires));
            issued = IssueCode(CodePurpose.Signup, e, null);
            return sent;
        });
        code = issued;
        return res;
    }

    /// <summary>Checks the sign-up code and creates the account. First verified wins a contested name.</summary>
    public Result VerifySignup(string email, string code, out Account? account)
    {
        account = null;
        if (!AccountRules.TryEmail(email, out var e, out _)) return Fail(BadCode);
        long id = 0;
        var res = Tx(() =>
        {
            var bad = UseCode(CodePurpose.Signup, e, null, code, out _);
            if (bad is not null) return bad;
            string name, hash, salt, expires;
            long iter;
            using (var cmd = Cmd("SELECT name, password_hash, password_salt, hash_iterations, expires FROM pending_signups WHERE email = $e", ("$e", e)))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return Fail(BadCode);
                name = r.GetString(0); hash = r.GetString(1); salt = r.GetString(2); iter = r.GetInt64(3); expires = r.GetString(4);
            }
            Run("DELETE FROM pending_signups WHERE email = $e", ("$e", e));
            if (Parse(expires) <= _clock()) return Fail(BadCode);
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE name = $n", ("$n", name)) > 0)
                return Fail($"Sorry, the name '{name}' was just taken. Please sign up with another name.");
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE recovery_email = $e", ("$e", e)) > 0)
                return Fail("That email is already used by another account.");
            Run("INSERT INTO accounts(name, password_hash, password_salt, hash_iterations, recovery_email, email_verified, created, status) "
                + "VALUES($n,$h,$s,$i,$e,1,$c,'active')",
                ("$n", name), ("$h", hash), ("$s", salt), ("$i", iter), ("$e", e), ("$c", Now()));
            id = Scalar("SELECT last_insert_rowid()");
            return Ok($"Welcome, {name}! Your account is ready.");
        });
        if (res.Ok) account = Get(id);
        return res;
    }

    // ---- game login ------------------------------------------------------

    /// <summary>
    /// Game login. Handles the imported legacy hash (re-hashed with PBKDF2 on success), the lockout
    /// (10 wrong passwords per name within 10 minutes refuses that name for 10 minutes, even with
    /// the right password) and LastLogin.
    /// </summary>
    public LoginResult CheckLogin(string name, string password, out Account? account)
    {
        account = null;
        password ??= "";
        Cred? cred;
        lock (_gate)
        {
            cred = LoadCred("name = $n", ("$n", name ?? ""));
            if (cred is null) return LoginResult.UnknownName;
            if (cred.Status != "active") return LoginResult.Disabled;
            if (IsLockedOut(cred.Name)) return LoginResult.LockedOut;
        }

        var ok = VerifyCred(cred, password);
        (string Hash, string Salt, int Iter)? rehash = ok && cred.Legacy is not null ? PasswordHasher.Hash(password) : null;

        var result = LoginResult.BadPassword;
        Tx(() =>
        {
            if (IsLockedOut(cred.Name)) { result = LoginResult.LockedOut; return Ok(""); }
            var now = _clock();
            if (!ok)
            {
                CountFailure(cred.Name, now);
                return Ok("");
            }
            var current = LoadCred("id = $id", ("$id", cred.Id));
            if (current is null || current.Hash != cred.Hash || current.Legacy != cred.Legacy || current.Status != "active")
                return Ok(""); // password changed (or account disabled) while we were verifying: refuse, don't count
            if (rehash is { } h)
                Run("UPDATE accounts SET password_hash=$h, password_salt=$s, hash_iterations=$i, legacy_hash=NULL WHERE id=$id",
                    ("$h", h.Hash), ("$s", h.Salt), ("$i", h.Iter), ("$id", cred.Id));
            Run("UPDATE accounts SET last_login = $t WHERE id = $id", ("$t", Fmt(now)), ("$id", cred.Id));
            Run("DELETE FROM login_failures WHERE name = $n", ("$n", cred.Name));
            result = LoginResult.Ok;
            return Ok("");
        });
        if (result == LoginResult.Ok) account = Get(cred.Id);
        return result;
    }

    // ---- recovery --------------------------------------------------------

    /// <summary>
    /// Starts password recovery. An unknown (or unverified) email gets the same Ok result with an
    /// empty <paramref name="code"/> and null <paramref name="name"/>. A newer request replaces the
    /// earlier code.
    /// </summary>
    public Result StartRecovery(string email, out string code, out string? name)
    {
        code = "";
        name = null;
        if (!AccountRules.TryEmail(email, out var e, out var err)) return Fail(err);
        var sent = SentMessage(e);
        string issued = "";
        string? found = null;
        var res = Tx(() =>
        {
            using (var cmd = Cmd("SELECT name FROM accounts WHERE recovery_email = $e AND email_verified = 1", ("$e", e)))
                found = cmd.ExecuteScalar() as string;
            if (found is null) return sent;
            issued = IssueCode(CodePurpose.Recovery, e, null);
            return sent;
        });
        code = issued;
        name = found;
        return res;
    }

    public Result CompleteRecovery(string email, string code, string newPassword)
    {
        if (!AccountRules.TryPassword(newPassword, out var err)) return Fail(err);
        if (!AccountRules.TryEmail(email, out var e, out _)) return Fail(BadCode);
        var (hash, salt, iter) = PasswordHasher.Hash(newPassword);
        return Tx(() =>
        {
            var bad = UseCode(CodePurpose.Recovery, e, null, code, out _);
            if (bad is not null) return bad;
            string? name;
            using (var cmd = Cmd("SELECT name FROM accounts WHERE recovery_email = $e", ("$e", e)))
                name = cmd.ExecuteScalar() as string;
            if (name is null) return Fail(BadCode);
            Run("UPDATE accounts SET password_hash=$h, password_salt=$s, hash_iterations=$i, legacy_hash=NULL WHERE recovery_email=$e",
                ("$h", hash), ("$s", salt), ("$i", iter), ("$e", e));
            Run("DELETE FROM login_failures WHERE name = $n", ("$n", name));
            return Ok("Your password was changed. You can log in with the new one now.");
        });
    }

    // ---- recovery email --------------------------------------------------

    /// <summary>
    /// Starts adding (or changing) the recovery email of an account. When the email belongs to
    /// another account, returns the same Ok result with an empty <paramref name="code"/>.
    /// </summary>
    public Result StartAddEmail(long accountId, string email, out string code)
    {
        code = "";
        if (!AccountRules.TryEmail(email, out var e, out var err)) return Fail(err);
        var sent = SentMessage(e);
        string issued = "";
        var res = Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id", ("$id", accountId)) == 0)
                return Fail("No such account.");
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE recovery_email = $e", ("$e", e)) > 0)
                return sent;
            issued = IssueCode(CodePurpose.AddEmail, e, accountId);
            return sent;
        });
        code = issued;
        return res;
    }

    public Result VerifyAddEmail(long accountId, string code) => Tx(() =>
    {
        var bad = UseCode(CodePurpose.AddEmail, null, accountId, code, out var e);
        if (bad is not null) return bad;
        if (Scalar("SELECT COUNT(*) FROM accounts WHERE recovery_email = $e AND id <> $id", ("$e", e!), ("$id", accountId)) > 0)
            return Fail("That email is already used by another account.");
        Run("UPDATE accounts SET recovery_email = $e, email_verified = 1 WHERE id = $id", ("$e", e!), ("$id", accountId));
        return Ok($"Your recovery email is now {e}.");
    });

    // ---- password --------------------------------------------------------

    public Result ChangePassword(long accountId, string current, string next)
    {
        if (!AccountRules.TryPassword(next, out var err)) return Fail(err);
        Cred? cred;
        lock (_gate) cred = LoadCred("id = $id", ("$id", accountId));
        if (cred is null) return Fail("No such account.");
        if (!VerifyCred(cred, current ?? "")) return Fail("Your current password is wrong.");
        var (hash, salt, iter) = PasswordHasher.Hash(next);
        return Tx(() =>
        {
            var now = LoadCred("id = $id", ("$id", accountId));
            if (now is null || now.Hash != cred.Hash || now.Legacy != cred.Legacy)
                return Fail("Your password was changed meanwhile. Please try again.");
            Run("UPDATE accounts SET password_hash=$h, password_salt=$s, hash_iterations=$i, legacy_hash=NULL WHERE id=$id",
                ("$h", hash), ("$s", salt), ("$i", iter), ("$id", accountId));
            return Ok("Your password was changed.");
        });
    }

    // ---- legacy import ---------------------------------------------------

    /// <summary>
    /// One-time import of passwd.conf [Users] entries (name → legacy hash). Skips names that already
    /// exist and the "lock"/"any" placeholders. Records a marker: later calls import nothing (0).
    /// </summary>
    public int ImportLegacy(IEnumerable<(string Name, string Hash)> users)
    {
        var list = users.ToList();
        var count = 0;
        Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM account_meta WHERE key = $k", ("$k", LegacyImportMarker)) > 0)
                return Ok("");
            var now = Now();
            foreach (var (rawName, rawHash) in list)
            {
                var name = (rawName ?? "").Trim();
                var hash = (rawHash ?? "").Trim();
                if (name.Length == 0 || hash.Length == 0) continue;
                if (hash.Equals("lock", StringComparison.OrdinalIgnoreCase) || hash.Equals("any", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Scalar("SELECT COUNT(*) FROM accounts WHERE name = $n", ("$n", name)) > 0) continue;
                Run("INSERT INTO accounts(name, hash_iterations, email_verified, created, status, legacy_hash) "
                    + "VALUES($n,0,0,$c,'active',$l)", ("$n", name), ("$c", now), ("$l", hash));
                count++;
            }
            Run("INSERT INTO account_meta(key, value) VALUES($k,$v)", ("$k", LegacyImportMarker), ("$v", $"{now} {count}"));
            return Ok("");
        });
        return count;
    }

    // ---- queries ---------------------------------------------------------

    public Account? Find(string name)
    {
        lock (_gate) return LoadAccount("name = $n", ("$n", name ?? ""));
    }

    public Account? Get(long id)
    {
        lock (_gate) return LoadAccount("id = $id", ("$id", id));
    }

    // ---- internals -------------------------------------------------------

    private sealed record Cred(long Id, string Name, string? Hash, string? Salt, int Iterations, string? Legacy, string Status);

    private static bool VerifyCred(Cred c, string password)
    {
        if (c.Hash is not null && c.Salt is not null)
            return PasswordHasher.Verify(password, c.Hash, c.Salt, c.Iterations);
        if (c.Legacy is not null)
            return PasswordHasher.VerifyLegacy(c.Name, password, c.Legacy);
        return false;
    }

    private Cred? LoadCred(string where, params (string, object)[] ps)
    {
        using var cmd = Cmd($"SELECT id, name, password_hash, password_salt, hash_iterations, legacy_hash, status FROM accounts WHERE {where}", ps);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Cred(r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3), r.GetInt32(4), Str(r, 5), r.GetString(6));
    }

    private Account? LoadAccount(string where, params (string, object)[] ps)
    {
        using var cmd = Cmd($"SELECT id, name, recovery_email, email_verified, created, last_login, isoj_name FROM accounts WHERE {where}", ps);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var last = Str(r, 5);
        return new Account(r.GetInt64(0), r.GetString(1), Str(r, 2), r.GetInt64(3) != 0, Parse(r.GetString(4)),
            last is null ? null : Parse(last), Str(r, 6));
    }

    private bool IsLockedOut(string name)
    {
        using var cmd = Cmd("SELECT locked_until FROM login_failures WHERE name = $n", ("$n", name));
        return cmd.ExecuteScalar() is string until && Parse(until) > _clock();
    }

    private void CountFailure(string name, DateTimeOffset now)
    {
        string? start = null;
        long count = 0;
        using (var cmd = Cmd("SELECT window_start, count FROM login_failures WHERE name = $n", ("$n", name)))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { start = r.GetString(0); count = r.GetInt64(1); }
        if (start is null || Parse(start) + LockoutWindow <= now)
        {
            start = Fmt(now);
            count = 0;
        }
        count++;
        string? lockedUntil = null;
        if (count >= MaxBadLogins)
        {
            lockedUntil = Fmt(now + LockoutDuration);
            start = Fmt(now);
            count = 0;
        }
        Run("INSERT OR REPLACE INTO login_failures(name, window_start, count, locked_until) VALUES($n,$w,$c,$l)",
            ("$n", name), ("$w", start), ("$c", count), ("$l", (object?)lockedUntil ?? DBNull.Value));
    }

    private static string PurposeKey(CodePurpose p) => p switch
    {
        CodePurpose.Signup => "signup",
        CodePurpose.Recovery => "recovery",
        CodePurpose.AddEmail => "add-email",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>SHA-256 hex over purpose + email + code.</summary>
    private static string CodeHash(CodePurpose purpose, string email, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PurposeKey(purpose) + email + code))).ToLowerInvariant();

    /// <summary>Replaces any earlier unused code for the same email+purpose (or account for AddEmail) and stores a new one's hash.</summary>
    private string IssueCode(CodePurpose purpose, string email, long? accountId)
    {
        var p = PurposeKey(purpose);
        if (accountId is { } id)
            Run("DELETE FROM email_codes WHERE purpose = $p AND (account_id = $id OR email = $e) AND used = 0", ("$p", p), ("$id", id), ("$e", email));
        else
            Run("DELETE FROM email_codes WHERE purpose = $p AND email = $e AND used = 0", ("$p", p), ("$e", email));
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        Run("INSERT INTO email_codes(purpose, email, account_id, code_hash, expires, attempts, used) VALUES($p,$e,$a,$h,$x,0,0)",
            ("$p", p), ("$e", email), ("$a", (object?)accountId ?? DBNull.Value), ("$h", CodeHash(purpose, email, code)),
            ("$x", Fmt(_clock() + CodeLifetime)));
        return code;
    }

    /// <summary>
    /// Checks a code inside the current transaction. Returns null and marks it used on a match;
    /// otherwise counts the wrong try (voiding the code at <see cref="MaxCodeAttempts"/>) and returns the failure.
    /// </summary>
    private Result? UseCode(CodePurpose purpose, string? email, long? accountId, string code, out string? codeEmail)
    {
        codeEmail = null;
        var p = PurposeKey(purpose);
        long id, attempts;
        string hash, expires, e;
        var sql = accountId is null
            ? "SELECT id, code_hash, expires, attempts, email FROM email_codes WHERE purpose = $p AND email = $k AND used = 0 ORDER BY id DESC LIMIT 1"
            : "SELECT id, code_hash, expires, attempts, email FROM email_codes WHERE purpose = $p AND account_id = $k AND used = 0 ORDER BY id DESC LIMIT 1";
        using (var cmd = Cmd(sql, ("$p", p), ("$k", (object?)accountId ?? email!)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return Fail(BadCode);
            id = r.GetInt64(0); hash = r.GetString(1); expires = r.GetString(2); attempts = r.GetInt64(3); e = r.GetString(4);
        }
        if (Parse(expires) <= _clock() || attempts >= MaxCodeAttempts)
        {
            Run("UPDATE email_codes SET used = 1 WHERE id = $id", ("$id", id));
            return Fail(BadCode);
        }
        var given = CodeHash(purpose, e, (code ?? "").Trim());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(hash)))
        {
            attempts++;
            Run("UPDATE email_codes SET attempts = $a, used = $u WHERE id = $id",
                ("$a", attempts), ("$u", attempts >= MaxCodeAttempts ? 1 : 0), ("$id", id));
            return Fail(BadCode);
        }
        Run("UPDATE email_codes SET used = 1 WHERE id = $id", ("$id", id));
        codeEmail = e;
        return null;
    }

    private static Result SentMessage(string email) => Ok($"If that's right, we sent a code to {email}.");

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Result Ok(string m) => new(true, m);
    private static Result Fail(string m) => new(false, m);
    private static string Fmt(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
    private string Now() => Fmt(_clock());

    /// <summary>
    /// Runs <paramref name="body"/> in one BEGIN IMMEDIATE transaction under the lock. Always commits
    /// (failed code tries and voided codes must persist even when the result is a failure); bodies
    /// check before they write. An exception rolls back.
    /// </summary>
    private Result Tx(Func<Result> body)
    {
        lock (_gate)
        {
            Exec("BEGIN IMMEDIATE");
            try
            {
                var res = body();
                Exec("COMMIT");
                return res;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT: a UNIQUE index caught a race
            {
                try { Exec("ROLLBACK"); } catch { }
                return Fail("That name or email is already taken.");
            }
            catch
            {
                try { Exec("ROLLBACK"); } catch { }
                throw;
            }
        }
    }

    private void Exec(string sql)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string, object)[] ps)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v);
        return c;
    }

    private void Run(string sql, params (string, object)[] ps)
    {
        using var c = Cmd(sql, ps);
        c.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string, object)[] ps)
    {
        using var c = Cmd(sql, ps);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
