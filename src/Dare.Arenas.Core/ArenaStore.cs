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
public sealed partial class ArenaStore : IDisposable
{
    public const int MaxCoMods = 5;
    /// <summary>Public arenas one pilot may own, drafts they may keep (not archived), and requests they may have waiting.</summary>
    /// <summary>Public arenas one account may own (all its names together) unless staff raised its limit.</summary>
    public const int MaxArenasPerAccount = 10;

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
              owner TEXT NOT NULL COLLATE NOCASE,
              status TEXT NOT NULL,
              created TEXT NOT NULL,
              last_entered TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS bots (
              arena TEXT NOT NULL COLLATE NOCASE,
              label TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
              script TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_bots_arena ON bots(arena);
            CREATE TABLE IF NOT EXISTS stopped_bots (
              label TEXT NOT NULL PRIMARY KEY COLLATE NOCASE
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
            CREATE TABLE IF NOT EXISTS drafts (
              name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
              owner TEXT NOT NULL COLLATE NOCASE,
              status TEXT NOT NULL,
              live INTEGER NOT NULL DEFAULT 0,
              created TEXT NOT NULL,
              last_touched TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_drafts_owner ON drafts(owner, status);
            CREATE TABLE IF NOT EXISTS draft_invites (
              name TEXT NOT NULL COLLATE NOCASE,
              player TEXT NOT NULL COLLATE NOCASE,
              PRIMARY KEY (name, player)
            );
            """);
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('requests') WHERE name = 'draft'") == 0)
            Exec("ALTER TABLE requests ADD COLUMN draft INTEGER NULL;");
        AllowSeveralArenasPerOwner();
        Exec("CREATE INDEX IF NOT EXISTS ix_arenas_owner ON arenas(owner);");
        EnsureTilesetTable();
        EnsureLvzTable();
        EnsureSettingSetTable();
        EnsureGameModeTable();
        // after the rebuild above, which keeps only the columns it knows
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('arenas') WHERE name = 'private'") == 0)
            Exec("ALTER TABLE arenas ADD COLUMN private INTEGER NOT NULL DEFAULT 0;");
        Exec("""
            CREATE TABLE IF NOT EXISTS pilot_maps (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              owner TEXT NOT NULL COLLATE NOCASE,
              name TEXT NOT NULL,
              data BLOB NOT NULL,
              updated TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_pilot_maps_owner ON pilot_maps(owner);
            CREATE TABLE IF NOT EXISTS bot_matches (
              arena TEXT NOT NULL COLLATE NOCASE,
              freq INTEGER NOT NULL,
              size INTEGER NOT NULL,
              PRIMARY KEY (arena, freq)
            );
            CREATE TABLE IF NOT EXISTS store_markers (
              key TEXT NOT NULL PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS restart_plans (
              arena TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
              at TEXT NOT NULL,
              every INTEGER NOT NULL,
              message TEXT NULL,
              by TEXT NOT NULL
            );
            """);
        // once: an arena that had bots before match sizes existed fields them all as team 0
        if (Scalar("SELECT COUNT(*) FROM store_markers WHERE key = 'bot_matches_migrated'") == 0)
        {
            Exec("""
                INSERT OR IGNORE INTO bot_matches(arena, freq, size)
                  SELECT arena, 0, MIN(COUNT(*), 16) FROM bots GROUP BY arena;
                INSERT INTO store_markers(key) VALUES('bot_matches_migrated');
                """);
        }
        AddLibraryPersonColumns();
        EnsureLimitTables();
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
        if (OwnedCount(player) >= ArenaLimitOf(player))
            return Fail(AtLimit(player, you: true));
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' is already taken.");
        if (Scalar("SELECT COUNT(*) FROM requests WHERE arena = $a AND status = 'Pending'", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' already has a pending request.");
        if (LoadDraft(arena) is { } d)
            return Fail(SameAccount(d.Owner, player)
                ? $"That's your draft #{d.Name}; ask with ?requestpublic {d.Name} <what it's for>."
                : $"Arena '{arena}' is already taken.");
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
        if (folderExists(arena))
            return Fail($"A folder named '{arena}' already exists on the server; cannot approve.");
        if (OwnedCount(req.Player) >= ArenaLimitOf(req.Player))
            return Fail(AtLimit(req.Player, you: false));
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", arena)) > 0)
            return Fail($"Arena '{arena}' is already taken.");
        var fromDraft = IsDraftRequestId(req.Id);
        var existingDraft = LoadDraft(arena);
        var folderError = createFolder();
        if (folderError is not null)
            return Fail($"Creating arenas/{arena}/ failed ({folderError}); nothing was approved and the request is still pending.");
        var now = Now();
        Run("UPDATE requests SET status='Approved', decided_by=$s WHERE id=$id", ("$s", staff), ("$id", req.Id));
        Run("INSERT INTO arenas(name, owner, status, created, last_entered) VALUES($a,$o,'Active',$t,$t)",
            ("$a", arena), ("$o", req.Player), ("$t", now));
        // a new arena has no draft any more; one asked for from an existing draft keeps it
        if (existingDraft is not null)
        {
            Run("UPDATE drafts SET status='Published', live=1, last_touched=$t WHERE name=$a", ("$t", now), ("$a", arena));
            // the draft's co-mods help run the live arena too
            Run("INSERT OR IGNORE INTO comods(arena, player) SELECT $a, player FROM comods WHERE arena = $d",
                ("$a", arena), ("$d", DraftRules.DraftOf(arena)));
        }
        AddEvent(now, arena, staff, "approve", $"owner: {req.Player}{(fromDraft ? " (from draft)" : "")}");
        return Ok($"Approved '{arena}' for {req.Player}.");
    });

    public Result Decline(string arena, string staff, string reason) => Tx(() =>
    {
        if (string.IsNullOrWhiteSpace(reason)) return Fail("A reason is required to decline a request.");
        var req = PendingRequest(arena, out var err);
        if (req is null) return Fail(err!);
        var now = Now();
        Run("UPDATE requests SET status='Declined', decided_by=$s, reason=$r WHERE id=$id",
            ("$s", staff), ("$r", reason), ("$id", req.Id));
        if (IsDraftRequestId(req.Id))
            Run("UPDATE drafts SET status='Active' WHERE name=$a AND status='Requested'", ("$a", arena));
        AddEvent(now, arena, staff, "decline", $"{req.Player}: {reason}");
        return Ok($"Declined '{arena}'.");
    });

    public Result AddCoMod(string arena, string owner, string player) => Tx(() =>
    {
        var a = DraftRules.IsDraft(arena) ? LoadDraftAsArena(arena) : Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (!SameAccount(a.Owner, owner)) return Fail("Only the arena owner can add co-mods.");
        if (string.IsNullOrWhiteSpace(player)) return Fail("Player name is required.");
        if (SameAccount(a.Owner, player)) return Fail("That's one of the owner's own names; it already has every owner power.");
        if (a.CoMods.Any(c => SameAccount(c, player))) return Fail($"{player} is already a co-mod.");
        if (a.CoMods.Count >= MaxCoMods) return Fail($"An arena can have at most {MaxCoMods} co-mods.");
        Run("INSERT INTO comods(arena, player) VALUES($a,$p)", ("$a", arena), ("$p", player));
        if (MirrorOf(arena) is { } other)
            Run("INSERT OR IGNORE INTO comods(arena, player) VALUES($a,$p)", ("$a", other), ("$p", player));
        AddEvent(Now(), arena, owner, "comod-add", player);
        return Ok($"{player} is now a co-mod of '{arena}'.");
    });

    public Result RemoveCoMod(string arena, string owner, string player) => Tx(() =>
    {
        var a = DraftRules.IsDraft(arena) ? LoadDraftAsArena(arena) : Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (!SameAccount(a.Owner, owner)) return Fail("Only the arena owner can remove co-mods.");
        if (!a.CoMods.Any(c => Same(c, player))) return Fail($"{player} is not a co-mod.");
        Run("DELETE FROM comods WHERE arena=$a AND player=$p", ("$a", arena), ("$p", player));
        if (MirrorOf(arena) is { } other)
            Run("DELETE FROM comods WHERE arena=$a AND player=$p", ("$a", other), ("$p", player));
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
        if (a.Status == ArenaStatus.Archived && OwnedCount(a.Owner) >= ArenaLimitOf(a.Owner))
            return Fail($"{a.Owner} already owns {ArenaLimitOf(a.Owner)} other arenas; transfer or archive one first, or raise their limit.");
        var folderError = ensureFolder();
        if (folderError is not null)
            return Fail($"Can't revive '{arena}': {folderError}");
        Run("DELETE FROM pending_archive_moves WHERE arena=$a", ("$a", arena));
        var now = Now();
        Run("UPDATE arenas SET status='Active', last_entered=$t WHERE name=$a", ("$t", now), ("$a", arena));
        // its live draft comes back with it
        Run("UPDATE drafts SET status='Published', last_touched=$t WHERE name=$a AND live=1 AND status='Archived'", ("$t", now), ("$a", arena));
        AddEvent(now, arena, staff, "revive", $"was {a.Status}");
        return Ok($"Revived '{arena}'.");
    });

    public Result Transfer(string arena, string staff, string newOwner) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null) return Fail($"No such arena '{arena}'.");
        if (string.IsNullOrWhiteSpace(newOwner)) return Fail("New owner is required.");
        if (OwnedCount(newOwner) >= ArenaLimitOf(newOwner))
            return Fail(AtLimit(newOwner, you: false));
        Run("DELETE FROM comods WHERE arena=$a AND player=$p", ("$a", arena), ("$p", newOwner));
        Run("UPDATE arenas SET owner=$o WHERE name=$a", ("$o", newOwner), ("$a", arena));
        // the draft goes with the arena; the old owner's invites go with the old owner
        var draft = DraftRules.DraftOf(arena);
        Run("DELETE FROM comods WHERE arena=$d AND player=$p", ("$d", draft), ("$p", newOwner));
        Run("UPDATE drafts SET owner=$o WHERE name=$a", ("$o", newOwner), ("$a", arena));
        Run("DELETE FROM draft_invites WHERE name=$a", ("$a", arena));
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
        ArchiveLiveDraft(arena);
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
                ArchiveLiveDraft(n);
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

    public string? RoleIn(string arena, string player)
    {
        var a = DraftRules.IsDraft(arena) ? DraftAsArena(arena) : Get(arena);
        if (a is null || a.Status != ArenaStatus.Active) return null;
        if (SameAccount(a.Owner, player)) return "owner";
        return a.CoMods.Any(c => SameAccount(c, player)) ? "comod" : null;
    }

    /// <summary>Active arenas where <paramref name="player"/> is owner or co-mod; owned arena first, then by name.</summary>
    public IReadOnlyList<(string Arena, string Role)> RolesFor(string player)
    {
        lock (_gate)
        {
            // every name of the player's account counts (an arena appears once, under its best role)
            var ps = new List<(string, object)>();
            var names = NamesOfAccount(player);
            var mine = string.Join(" UNION ALL ", names.Select((n, i) => { ps.Add(($"$m{i}", n)); return $"SELECT $m{i} AS n"; }));
            using var cmd = Cmd("WITH mine(n) AS (" + mine + ") " + """
                SELECT DISTINCT name, role, ord FROM (
                SELECT name, 'owner' AS role, 1 AS ord FROM arenas WHERE owner IN (SELECT n FROM mine) AND status = 'Active'
                UNION ALL
                SELECT a.name, 'comod', 3 FROM comods c JOIN arenas a ON a.name = c.arena
                  WHERE c.player IN (SELECT n FROM mine) AND a.status = 'Active' AND a.owner NOT IN (SELECT n FROM mine)
                UNION ALL
                SELECT '#' || d.name, 'owner', 0 FROM drafts d WHERE d.owner IN (SELECT n FROM mine) AND d.status <> 'Archived'
                  AND (d.live = 0 OR EXISTS (SELECT 1 FROM arenas a WHERE a.name = d.name AND a.status = 'Active'))
                UNION ALL
                SELECT '#' || d.name, 'comod', 2 FROM comods c JOIN drafts d ON c.arena = '#' || d.name
                  WHERE c.player IN (SELECT n FROM mine) AND d.status <> 'Archived' AND d.owner NOT IN (SELECT n FROM mine)
                  AND (d.live = 0 OR EXISTS (SELECT 1 FROM arenas a WHERE a.name = d.name AND a.status = 'Active'))
                ) ORDER BY ord, name
                """, [.. ps]);
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
