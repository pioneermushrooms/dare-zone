namespace Dare.Arenas.Core;

/// <summary>A pilot's ask for more public arenas, and what staff decided.</summary>
public sealed record ArenaLimitRequest(
    long Id, long Person, string Player, int Current, int Wanted, string Reason, string Status,
    DateTimeOffset At, string? DecidedBy, int? Granted, string? Note);

// Each account may own MaxArenasPerAccount public arenas (all its names together; the #name working
// copies don't count). At the limit a pilot can ask for more: staff see the ask on the staff page,
// pick a number and approve it, which raises that account's limit; or decline it with a reason.
public sealed partial class ArenaStore
{
    public const int MaxLimitReasonLength = 300;
    /// <summary>The most staff can raise one account to.</summary>
    public const int MaxArenaLimit = 200;

    private void EnsureLimitTables() => Exec("""
        CREATE TABLE IF NOT EXISTS arena_limits (
          person_id INTEGER NOT NULL PRIMARY KEY,
          max INTEGER NOT NULL,
          by TEXT NOT NULL,
          at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS arena_limit_requests (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          person_id INTEGER NOT NULL,
          player TEXT NOT NULL COLLATE NOCASE,
          current INTEGER NOT NULL,
          wanted INTEGER NOT NULL,
          reason TEXT NOT NULL,
          status TEXT NOT NULL,
          at TEXT NOT NULL,
          decided_by TEXT NULL,
          granted INTEGER NULL,
          note TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_arena_limit_requests_status ON arena_limit_requests(status, id);
        """);

    /// <summary>How many public arenas <paramref name="player"/>'s account may own.</summary>
    public int ArenaLimitOf(string player)
    {
        lock (_gate)
        {
            if (PersonOfName(player) is not { } p) return MaxArenasPerAccount;
            using var c = Cmd("SELECT max FROM arena_limits WHERE person_id = $p", ("$p", p));
            return c.ExecuteScalar() is long m ? (int)m : MaxArenasPerAccount;
        }
    }

    /// <summary>Public arenas (not archived) <paramref name="player"/>'s account owns now.</summary>
    public int OwnedCountOf(string player)
    {
        lock (_gate) return (int)OwnedCount(player);
    }

    private string AtLimit(string player, bool you) => you
        ? $"You already own {ArenaLimitOf(player)} arenas, your limit. Ask for more on your arena page (Request more arenas)."
        : $"{player} already owns {ArenaLimitOf(player)} arenas, their limit.";

    /// <summary>
    /// Asks staff for more arenas for <paramref name="player"/>'s account: only at the limit, one
    /// open ask at a time, <paramref name="wanted"/> above the current limit.
    /// </summary>
    public Result RequestMoreArenas(string player, int wanted, string? reason, out long id)
    {
        id = 0;
        reason = (reason ?? "").Trim();
        if (reason.Any(char.IsControl)) return Fail("Keep the reason to one line.");
        if (reason.Length > MaxLimitReasonLength) return Fail($"Keep the reason to {MaxLimitReasonLength} characters.");
        long newId = 0;
        var r = Tx(() =>
        {
            if (PersonOfName(player) is not { } p) return Fail("Sign in with your Foundation account first.");
            var limit = ArenaLimitOf(player);
            if (OwnedCount(player) < limit) return Fail($"You can still make {limit - OwnedCount(player)} more arenas.");
            if (wanted <= limit || wanted > MaxArenaLimit) return Fail($"Ask for a number from {limit + 1} to {MaxArenaLimit}.");
            if (Scalar("SELECT COUNT(*) FROM arena_limit_requests WHERE person_id = $p AND status = 'Pending'", ("$p", p)) > 0)
                return Fail("You already asked; staff will get back to you.");
            Run("INSERT INTO arena_limit_requests(person_id, player, current, wanted, reason, status, at) VALUES($p,$n,$c,$w,$r,'Pending',$t)",
                ("$p", p), ("$n", player), ("$c", limit), ("$w", wanted), ("$r", reason), ("$t", Now()));
            newId = Scalar("SELECT last_insert_rowid()");
            return Ok($"Asked staff for up to {wanted} arenas. You'll hear back here and in game.");
        });
        id = newId;
        return r;
    }

    /// <summary>The account's open ask for more arenas, or null.</summary>
    public ArenaLimitRequest? PendingLimitRequestOf(string player)
    {
        lock (_gate)
        {
            if (PersonOfName(player) is not { } p) return null;
            return LimitRequests("WHERE person_id = $p AND status = 'Pending'", 1, ("$p", p)).FirstOrDefault();
        }
    }

    /// <summary>The account's latest decided ask (to show the answer), or null.</summary>
    public ArenaLimitRequest? LastLimitAnswerOf(string player)
    {
        lock (_gate)
        {
            if (PersonOfName(player) is not { } p) return null;
            return LimitRequests("WHERE person_id = $p AND status <> 'Pending'", 1, ("$p", p)).FirstOrDefault();
        }
    }

    /// <summary>Asks for more arenas, newest first: the open ones only, or all.</summary>
    public IReadOnlyList<ArenaLimitRequest> LimitRequests(bool pendingOnly, int max)
    {
        lock (_gate) return LimitRequests(pendingOnly ? "WHERE status = 'Pending'" : "", max);
    }

    public ArenaLimitRequest? LimitRequest(long id)
    {
        lock (_gate) return LimitRequests("WHERE id = $id", 1, ("$id", id)).FirstOrDefault();
    }

    /// <summary>Staff approve an ask: the account's limit becomes <paramref name="granted"/>.</summary>
    public Result ApproveLimit(long id, string staff, int granted) => Tx(() =>
    {
        var req = LimitRequests("WHERE id = $id", 1, ("$id", id)).FirstOrDefault();
        if (req is null) return Fail("No such request.");
        if (req.Status != "Pending") return Fail("That request was already answered.");
        if (granted < 1 || granted > MaxArenaLimit) return Fail($"Pick a limit from 1 to {MaxArenaLimit}.");
        var now = Now();
        Run("INSERT OR REPLACE INTO arena_limits(person_id, max, by, at) VALUES($p,$m,$b,$t)",
            ("$p", req.Person), ("$m", granted), ("$b", staff), ("$t", now));
        Run("UPDATE arena_limit_requests SET status = 'Approved', decided_by = $b, granted = $m WHERE id = $id",
            ("$b", staff), ("$m", granted), ("$id", id));
        AddEvent(now, "", staff, "arena-limit", $"{req.Player}: {req.Current} -> {granted}");
        return Ok($"{req.Player}'s account may now own {granted} arenas.");
    });

    public Result DeclineLimit(long id, string staff, string? note) => Tx(() =>
    {
        var req = LimitRequests("WHERE id = $id", 1, ("$id", id)).FirstOrDefault();
        if (req is null) return Fail("No such request.");
        if (req.Status != "Pending") return Fail("That request was already answered.");
        if (string.IsNullOrWhiteSpace(note)) return Fail("Say why, for the pilot.");
        Run("UPDATE arena_limit_requests SET status = 'Declined', decided_by = $b, note = $n WHERE id = $id",
            ("$b", staff), ("$n", note.Trim()), ("$id", id));
        AddEvent(Now(), "", staff, "arena-limit-decline", $"{req.Player}: {note.Trim()}");
        return Ok($"Declined {req.Player}'s request.");
    });

    /// <summary>Staff set an account's limit directly (no request needed).</summary>
    public Result SetArenaLimit(string player, string staff, int max) => Tx(() =>
    {
        if (PersonOfName(player) is not { } p) return Fail($"{player} has no Foundation account.");
        if (max < 1 || max > MaxArenaLimit) return Fail($"Pick a limit from 1 to {MaxArenaLimit}.");
        Run("INSERT OR REPLACE INTO arena_limits(person_id, max, by, at) VALUES($p,$m,$b,$t)",
            ("$p", p), ("$m", max), ("$b", staff), ("$t", Now()));
        AddEvent(Now(), "", staff, "arena-limit", $"{player}: set to {max}");
        return Ok($"{player}'s account may now own {max} arenas.");
    });

    private List<ArenaLimitRequest> LimitRequests(string where, int max, params (string, object)[] ps)
    {
        var list = new List<ArenaLimitRequest>();
        using var c = Cmd($"SELECT id, person_id, player, current, wanted, reason, status, at, decided_by, granted, note FROM arena_limit_requests {where} ORDER BY id DESC LIMIT $max",
            [.. ps, ("$max", max)]);
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new ArenaLimitRequest(r.GetInt64(0), r.GetInt64(1), r.GetString(2), (int)r.GetInt64(3), (int)r.GetInt64(4),
                r.GetString(5), r.GetString(6), Parse(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : (int)r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10)));
        return list;
    }
}
