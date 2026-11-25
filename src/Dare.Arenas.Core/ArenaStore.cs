using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Core;

/// <summary>
/// SQLite-backed store for arena requests, ownership and lifecycle. Arena names are expected
/// to be already normalized (lower-case) by the caller; player names keep their original
/// casing and compare case-insensitively. Every mutation runs in one BEGIN IMMEDIATE
/// transaction and appends exactly one event (ArchiveIdle: one per archived arena;
/// ArchiveMoveDone is bookkeeping and appends none). Archiving records a pending folder move
/// (table pending_archive_moves) that the module carries out at its next start.
/// </summary>
public sealed class ArenaStore : IDisposable
{
    public const int MaxCoMods = 5;

    private readonly SqliteConnection _conn;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public ArenaStore(string dbPath, Func<DateTimeOffset> clock)
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
            CREATE TABLE IF NOT EXISTS requests (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              arena TEXT NOT NULL COLLATE NOCASE,
              player TEXT NOT NULL COLLATE NOCASE,
              pitch TEXT NOT NULL,
              status TEXT NOT NULL,
              decided_by TEXT NULL,
              reason TEXT NULL,
              at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS arenas (
              name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
              owner TEXT NOT NULL UNIQUE COLLATE NOCASE,
              status TEXT NOT NULL,
              created TEXT NOT NULL,
              last_entered TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS comods (
              arena TEXT NOT NULL COLLATE NOCASE,
              player TEXT NOT NULL COLLATE NOCASE,
              PRIMARY KEY (arena, player)
            );
            CREATE TABLE IF NOT EXISTS events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              at TEXT NOT NULL,
              arena TEXT NOT NULL COLLATE NOCASE,
              actor TEXT NOT NULL,
              kind TEXT NOT NULL,
              detail TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pending_archive_moves (
              arena TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
              at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_requests_player ON requests(player, status);
            CREATE INDEX IF NOT EXISTS ix_requests_arena ON requests(arena, status);
            CREATE INDEX IF NOT EXISTS ix_events_arena ON events(arena, id);
            """);
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }

    // ---- mutations -------------------------------------------------------

    public Result Request(string player, string arena, string pitch) => Tx(() =>
    {
        if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(arena))
            return Fail("Player and arena name are required.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE owner = $p", ("$p", player)) > 0)
            return Fail("You already own an arena.");
        if (Scalar("SELECT COUNT(*) FROM requests WHERE player = $p AND status = 'Pending'", ("$p", player)) > 0)
            return Fail("You already have a pending arena request.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' is already taken.");
        if (Scalar("SELECT COUNT(*) FROM requests WHERE arena = $a AND status = 'Pending'", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' already has a pending request.");
        var now = Now();
        Run("INSERT INTO requests(arena, player, pitch, status, at) VALUES($a,$p,$pitch,'Pending',$at)",
            ("$a", arena), ("$p", player), ("$pitch", pitch ?? ""), ("$at", now));
        AddEvent(now, arena, player, "request", pitch ?? "");
        return Ok($"Request for '{arena}' submitted.");
    });

    public Result Approve(string arena, string staff, Func<string, bool> folderExists) =>
        Approve(arena, staff, folderExists, () => null);

    /// <summary>
    /// Approves the pending request for <paramref name="arena"/>. <paramref name="createFolder"/> runs
    /// inside the transaction after every check passed; when it returns an error the approval is
    /// rolled back (the request stays pending), so there is never an Active arena without a folder.
    /// </summary>
    public Result Approve(string arena, string staff, Func<string, bool> folderExists, Func<string?> createFolder) => Tx(() =>
    {
        var req = PendingRequest(arena, out var err);
        if (req is null) return Fail(err!);
        if (Same(req.Player, staff))
            return Fail("You can't decide your own request; another staff member must.");
        if (folderExists(arena))
            return Fail($"A folder named '{arena}' already exists on the server; cannot approve.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE owner = $p", ("$p", req.Player)) > 0)
            return Fail($"{req.Player} already owns an arena.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' is already taken.");
        var folderError = createFolder();
        if (folderError is not null)
            return Fail($"Creating arenas/{arena}/ failed ({folderError}); nothing was approved and the request is still pending.");
        var now = Now();
        Run("UPDATE requests SET status='Approved', decided_by=$s WHERE id=$id", ("$s", staff), ("$id", req.Id));
        Run("INSERT INTO arenas(name, owner, status, created, last_entered) VALUES($a,$o,'Active',$t,$t)",
            ("$a", arena), ("$o", req.Player), ("$t", now));
        AddEvent(now, arena, staff, "approve", $"owner: {req.Player}");
        return Ok($"Approved '{arena}' for {req.Player}.");
    });

    /// <summary>
    /// Staff create an arena directly (no request): <paramref name="owner"/> owns it at once. Same
    /// checks as <see cref="Approve(string, string, Func{string, bool}, Func{string?})"/> on the
    /// owner and the name, and refuses a name someone has a pending request for (decide that first)
    /// and an owner with a pending request (approving it later would fail). <paramref name="createFolder"/>
    /// runs inside the transaction after every check passed; on error nothing is created.
    /// </summary>
    public Result Create(string arena, string staff, string owner, Func<string, bool> folderExists, Func<string?> createFolder) => Tx(() =>
    {
        if (string.IsNullOrWhiteSpace(arena) || string.IsNullOrWhiteSpace(staff) || string.IsNullOrWhiteSpace(owner))
            return Fail("Arena name, staff and owner are required.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' is already taken.");
        if (Scalar("SELECT COUNT(*) FROM requests WHERE arena = $a AND status = 'Pending'", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' has a pending request; approve or decline it instead.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE owner = $p", ("$p", owner)) > 0)
            return Fail($"{owner} already owns an arena.");
        if (Scalar("SELECT COUNT(*) FROM requests WHERE player = $p AND status = 'Pending'", ("$p", owner)) > 0)
            return Fail($"{owner} has a pending arena request; approve or decline it first.");
        if (folderExists(arena))
            return Fail($"A folder named '{arena}' already exists on the server; cannot create.");
        var folderError = createFolder();
        if (folderError is not null)
            return Fail($"Creating arenas/{arena}/ failed ({folderError}); nothing was created.");
        var now = Now();
        Run("INSERT INTO arenas(name, owner, status, created, last_entered) VALUES($a,$o,'Active',$t,$t)",
            ("$a", arena), ("$o", owner), ("$t", now));
        AddEvent(now, arena, staff, "create", $"owner: {owner}");
        return Ok($"Created '{arena}' for {owner}.");
    });

    /// <summary>
    /// A player launches an arena themselves (no staff approval): they own it at once. The limits are
    /// the ones a request had: one arena per player, a free name, no other player's pending request
    /// for it (staff decide those first). The player's own pending request, if any, is closed: approved
    /// when it is for this name, withdrawn (declined by themselves) otherwise. The launch is recorded
    /// as an approved request (decided by the player) so it shows in the public list, plus a
    /// <c>launch</c> event. <paramref name="createFolder"/> runs inside the transaction after every
    /// check passed; on error nothing changes.
    /// </summary>
    public Result Launch(string arena, string player, string pitch, Func<string, bool> folderExists, Func<string?> createFolder) => Tx(() =>
    {
        if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(arena))
            return Fail("Player and arena name are required.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE owner = $p", ("$p", player)) > 0)
            return Fail("You already own an arena.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' is already taken.");
        if (Scalar("SELECT COUNT(*) FROM requests WHERE arena = $a AND status = 'Pending' AND player <> $p",
                ("$a", arena), ("$p", player)) > 0)
            return Fail($"Someone already asked for '{arena}'; pick another name.");
        if (folderExists(arena))
            return Fail($"A folder named '{arena}' already exists on the server; pick another name.");
        var folderError = createFolder();
        if (folderError is not null)
            return Fail($"Creating arenas/{arena}/ failed ({folderError}); nothing was created.");

        var now = Now();
        pitch ??= "";
        var ownRequest = Scalar("SELECT COUNT(*) FROM requests WHERE player = $p AND status = 'Pending' AND arena = $a",
            ("$p", player), ("$a", arena)) > 0;
        Run("UPDATE requests SET status='Declined', decided_by=$p, reason=$r WHERE player = $p AND status = 'Pending' AND arena <> $a",
            ("$p", player), ("$a", arena), ("$r", $"withdrawn: launched {arena}"));
        if (ownRequest)
            Run("UPDATE requests SET status='Approved', decided_by=$p WHERE player = $p AND status = 'Pending' AND arena = $a",
                ("$p", player), ("$a", arena));
        else
            Run("INSERT INTO requests(arena, player, pitch, status, decided_by, at) VALUES($a,$p,$pitch,'Approved',$p,$at)",
                ("$a", arena), ("$p", player), ("$pitch", pitch), ("$at", now));
        Run("INSERT INTO arenas(name, owner, status, created, last_entered) VALUES($a,$o,'Active',$t,$t)",
            ("$a", arena), ("$o", player), ("$t", now));
        AddEvent(now, arena, player, "launch", pitch);
        return Ok($"Launched '{arena}'. It's yours.");
    });

    public Result Decline(string arena, string staff, string reason) => Tx(() =>
    {
        if (string.IsNullOrWhiteSpace(reason)) return Fail("A reason is required to decline a request.");
        var req = PendingRequest(arena, out var err);
        if (req is null) return Fail(err!);
        if (Same(req.Player, staff))
            return Fail("You can't decide your own request; another staff member must.");
        var now = Now();
        Run("UPDATE requests SET status='Declined', decided_by=$s, reason=$r WHERE id=$id",
            ("$s", staff), ("$r", reason), ("$id", req.Id));
        AddEvent(now, arena, staff, "decline", $"{req.Player}: {reason}");
        return Ok($"Declined '{arena}'.");
    });

    public Result AddCoMod(string arena, string owner, string player) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (!Same(a.Owner, owner)) return Fail("Only the arena owner can add co-mods.");
        if (string.IsNullOrWhiteSpace(player)) return Fail("Player name is required.");
        if (Same(a.Owner, player)) return Fail("The owner cannot be their own co-mod.");
        if (a.CoMods.Any(c => Same(c, player))) return Fail($"{player} is already a co-mod.");
        if (a.CoMods.Count >= MaxCoMods) return Fail($"An arena can have at most {MaxCoMods} co-mods.");
        Run("INSERT INTO comods(arena, player) VALUES($a,$p)", ("$a", arena), ("$p", player));
        AddEvent(Now(), arena, owner, "comod-add", player);
        return Ok($"{player} is now a co-mod of '{arena}'.");
    });

    public Result RemoveCoMod(string arena, string owner, string player) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (!Same(a.Owner, owner)) return Fail("Only the arena owner can remove co-mods.");
        if (!a.CoMods.Any(c => Same(c, player))) return Fail($"{player} is not a co-mod.");
        Run("DELETE FROM comods WHERE arena=$a AND player=$p", ("$a", arena), ("$p", player));
        AddEvent(Now(), arena, owner, "comod-remove", player);
        return Ok($"{player} is no longer a co-mod of '{arena}'.");
    });

    public Result Suspend(string arena, string staff, string reason) => Tx(() =>
    {
        if (string.IsNullOrWhiteSpace(reason)) return Fail("A reason is required to suspend an arena.");
        var a = Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (a.Status != ArenaStatus.Active) return Fail($"Arena '{arena}' is not active.");
        Run("UPDATE arenas SET status='Suspended' WHERE name=$a", ("$a", arena));
        AddEvent(Now(), arena, staff, "suspend", reason);
        return Ok($"Suspended '{arena}'.");
    });

    public Result Revive(string arena, string staff) => Revive(arena, staff, () => null);

    /// <summary>
    /// Suspended or Archived → Active. <paramref name="ensureFolder"/> runs inside the transaction
    /// and must make sure <c>arenas/&lt;arena&gt;/</c> exists (restoring it from the archive if needed);
    /// when it returns an error nothing changes. A pending archive folder move is cancelled.
    /// </summary>
    public Result Revive(string arena, string staff, Func<string?> ensureFolder) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (a.Status == ArenaStatus.Active) return Fail($"Arena '{arena}' is already active.");
        var folderError = ensureFolder();
        if (folderError is not null)
            return Fail($"Can't revive '{arena}': {folderError}");
        Run("DELETE FROM pending_archive_moves WHERE arena=$a", ("$a", arena));
        var now = Now();
        Run("UPDATE arenas SET status='Active', last_entered=$t WHERE name=$a", ("$t", now), ("$a", arena));
        AddEvent(now, arena, staff, "revive", $"was {a.Status}");
        return Ok($"Revived '{arena}'.");
    });

    public Result Transfer(string arena, string staff, string newOwner) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (string.IsNullOrWhiteSpace(newOwner)) return Fail("New owner is required.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE owner = $p", ("$p", newOwner)) > 0)
            return Fail($"{newOwner} already owns an arena.");
        Run("DELETE FROM comods WHERE arena=$a AND player=$p", ("$a", arena), ("$p", newOwner));
        Run("UPDATE arenas SET owner=$o WHERE name=$a", ("$o", newOwner), ("$a", arena));
        AddEvent(Now(), arena, staff, "transfer", $"{a.Owner} -> {newOwner}");
        return Ok($"Transferred '{arena}' to {newOwner}.");
    });

    /// <summary>Staff archive (Active or Suspended → Archived); ownership and co-mods are kept.</summary>
    public Result Archive(string arena, string staff) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (a.Status == ArenaStatus.Archived) return Fail($"Arena '{arena}' is already archived.");
        var now = Now();
        Run("UPDATE arenas SET status='Archived' WHERE name=$a", ("$a", arena));
        AddPendingMove(arena, now);
        AddEvent(now, arena, staff, "archive", $"by staff (was {a.Status})");
        return Ok($"Archived '{arena}'.");
    });

    public IReadOnlyList<string> ArchiveIdle(TimeSpan idle) => ArchiveIdle(idle, _ => false);

    /// <summary>
    /// Archives every Active arena nobody entered for <paramref name="idle"/>, except those
    /// <paramref name="skip"/> returns true for (e.g. arenas with players in them right now).
    /// Each gets a pending folder move. Returns the archived names.
    /// </summary>
    public IReadOnlyList<string> ArchiveIdle(TimeSpan idle, Func<string, bool> skip)
    {
        var archived = new List<string>();
        Tx(() =>
        {
            var nowDto = _clock();
            var cutoff = Fmt(nowDto - idle);
            var now = Fmt(nowDto);
            var names = new List<string>();
            using (var cmd = Cmd("SELECT name FROM arenas WHERE status='Active' AND last_entered < $c ORDER BY name",
                       ("$c", cutoff)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) names.Add(r.GetString(0));
            foreach (var n in names)
            {
                if (skip(n)) continue;
                Run("UPDATE arenas SET status='Archived' WHERE name=$a", ("$a", n));
                AddPendingMove(n, now);
                AddEvent(now, n, "system", "archive", $"idle more than {(int)idle.TotalDays} days");
                archived.Add(n);
            }
            return Ok("");
        });
        return archived;
    }

    public void MarkEntered(string arena) => Tx(() =>
    {
        Run("UPDATE arenas SET last_entered=$t WHERE name=$a AND status='Active'", ("$t", Now()), ("$a", arena));
        return Ok("");
    });

    /// <summary>Forgets a pending archive folder move (done, or no longer needed).</summary>
    public void ArchiveMoveDone(string arena) => Tx(() =>
    {
        Run("DELETE FROM pending_archive_moves WHERE arena=$a", ("$a", arena));
        return Ok("");
    });

    // ---- queries ---------------------------------------------------------

    public OwnedArena? Get(string arena)
    {
        lock (_gate) return Load(arena);
    }

    public OwnedArena? OwnedBy(string player)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT name FROM arenas WHERE owner = $p", ("$p", player));
            var name = cmd.ExecuteScalar() as string;
            return name is null ? null : Load(name);
        }
    }

    public string? RoleIn(string arena, string player)
    {
        var a = Get(arena);
        if (a is null || a.Status != ArenaStatus.Active) return null;
        if (Same(a.Owner, player)) return "owner";
        return a.CoMods.Any(c => Same(c, player)) ? "comod" : null;
    }

    /// <summary>Active arenas where <paramref name="player"/> is owner or co-mod; owned arena first, then by name.</summary>
    public IReadOnlyList<(string Arena, string Role)> RolesFor(string player)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                SELECT name, 'owner' AS role, 0 AS ord FROM arenas WHERE owner = $p AND status = 'Active'
                UNION ALL
                SELECT a.name, 'comod', 1 FROM comods c JOIN arenas a ON a.name = c.arena
                  WHERE c.player = $p AND a.status = 'Active'
                ORDER BY ord, name
                """, ("$p", player));
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>Every player-owned arena (any status), by name. Two queries, not one per arena.</summary>
    public IReadOnlyList<OwnedArena> Arenas()
    {
        lock (_gate)
        {
            var comods = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = Cmd("SELECT arena, player FROM comods ORDER BY rowid"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (!comods.TryGetValue(r.GetString(0), out var l)) comods[r.GetString(0)] = l = new List<string>();
                    l.Add(r.GetString(1));
                }

            var list = new List<OwnedArena>();
            using (var cmd = Cmd("SELECT name, owner, status, created, last_entered FROM arenas ORDER BY name"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var name = r.GetString(0);
                    list.Add(new OwnedArena(name, r.GetString(1), Enum.Parse<ArenaStatus>(r.GetString(2)),
                        Parse(r.GetString(3)), Parse(r.GetString(4)),
                        comods.TryGetValue(name, out var c) ? c : new List<string>()));
                }
            return list;
        }
    }

    public IReadOnlyList<ArenaRequest> Requests(bool pendingOnly, int max)
    {
        lock (_gate)
        {
            var sql = "SELECT id, arena, player, pitch, status, decided_by, reason, at FROM requests "
                      + (pendingOnly ? "WHERE status='Pending' " : "")
                      + "ORDER BY id DESC LIMIT $m";
            using var cmd = Cmd(sql, ("$m", max));
            using var r = cmd.ExecuteReader();
            var list = new List<ArenaRequest>();
            while (r.Read()) list.Add(ReadRequest(r));
            return list;
        }
    }

    /// <summary>Arenas archived since the last start whose folder still has to move to the archive, by name.</summary>
    public IReadOnlyList<string> PendingArchiveMoves()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT arena FROM pending_archive_moves ORDER BY arena");
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>Pending requests, the longest-waiting first (at most <paramref name="max"/>).</summary>
    public IReadOnlyList<ArenaRequest> PendingRequestsOldestFirst(int max)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT id, arena, player, pitch, status, decided_by, reason, at FROM requests "
                                + "WHERE status='Pending' ORDER BY id ASC LIMIT $m", ("$m", max));
            using var r = cmd.ExecuteReader();
            var list = new List<ArenaRequest>();
            while (r.Read()) list.Add(ReadRequest(r));
            return list;
        }
    }

    public IReadOnlyList<ArenaEvent> Events(string? arena, int max)
    {
        lock (_gate)
        {
            using var cmd = arena is null
                ? Cmd("SELECT id, at, arena, actor, kind, detail FROM events ORDER BY id DESC LIMIT $m", ("$m", max))
                : Cmd("SELECT id, at, arena, actor, kind, detail FROM events WHERE arena = $a ORDER BY id DESC LIMIT $m",
                    ("$a", arena), ("$m", max));
            using var r = cmd.ExecuteReader();
            var list = new List<ArenaEvent>();
            while (r.Read())
                list.Add(new ArenaEvent(r.GetInt64(0), Parse(r.GetString(1)), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5)));
            return list;
        }
    }

    // ---- internals -------------------------------------------------------

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static Result Ok(string m) => new(true, m);
    private static Result Fail(string m) => new(false, m);
    private static string Fmt(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
    private string Now() => Fmt(_clock());

    private Result Tx(Func<Result> body)
    {
        lock (_gate)
        {
            Exec("BEGIN IMMEDIATE");
            try
            {
                var res = body();
                Exec(res.Ok ? "COMMIT" : "ROLLBACK");
                return res;
            }
            catch
            {
                try { Exec("ROLLBACK"); } catch { }
                throw;
            }
        }
    }

    private ArenaRequest? PendingRequest(string arena, out string? error)
    {
        using var cmd = Cmd("SELECT id, arena, player, pitch, status, decided_by, reason, at FROM requests "
                            + "WHERE arena = $a ORDER BY id DESC LIMIT 1", ("$a", arena));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { error = $"No request for '{arena}'."; return null; }
        var req = ReadRequest(r);
        if (req.Status != RequestStatus.Pending) { error = $"Request for '{arena}' was already decided."; return null; }
        error = null;
        return req;
    }

    private static ArenaRequest ReadRequest(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        Enum.Parse<RequestStatus>(r.GetString(4)),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), Parse(r.GetString(7)));

    private OwnedArena? Load(string arena)
    {
        string name, owner, status, created, last;
        using (var cmd = Cmd("SELECT name, owner, status, created, last_entered FROM arenas WHERE name = $a", ("$a", arena)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            name = r.GetString(0); owner = r.GetString(1); status = r.GetString(2);
            created = r.GetString(3); last = r.GetString(4);
        }
        var comods = new List<string>();
        using (var cmd = Cmd("SELECT player FROM comods WHERE arena = $a ORDER BY rowid", ("$a", arena)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) comods.Add(r.GetString(0));
        return new OwnedArena(name, owner, Enum.Parse<ArenaStatus>(status), Parse(created), Parse(last), comods);
    }

    private void AddPendingMove(string arena, string at) =>
        Run("INSERT OR REPLACE INTO pending_archive_moves(arena, at) VALUES($a,$at)", ("$a", arena), ("$at", at));

    private void AddEvent(string at, string arena, string actor, string kind, string detail) =>
        Run("INSERT INTO events(at, arena, actor, kind, detail) VALUES($at,$a,$ac,$k,$d)",
            ("$at", at), ("$a", arena), ("$ac", actor), ("$k", kind), ("$d", detail));

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
