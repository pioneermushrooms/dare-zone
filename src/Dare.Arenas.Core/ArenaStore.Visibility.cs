namespace Dare.Arenas.Core;

/// <summary>
/// Owners decide: an arena is public (listed on the site) or private (left off the site's lists;
/// anyone who knows the name can still enter). And an owner can discard an arena: it is archived
/// (closed, its files stored away, revivable by staff), as the idle archive does.
/// </summary>
public sealed partial class ArenaStore
{
    public bool IsPrivate(string arena)
    {
        lock (_gate)
            return Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a AND private = 1", ("$a", arena)) > 0;
    }

    /// <summary>The names of the arenas their owners made private.</summary>
    public IReadOnlySet<string> PrivateArenas()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            using var c = Cmd("SELECT name FROM arenas WHERE private = 1");
            using var r = c.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        return set;
    }

    /// <summary><paramref name="owner"/> makes <paramref name="arena"/> private or public. No reason needed.</summary>
    public Result SetPrivate(string arena, string owner, bool makePrivate) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null || !SameAccount(a.Owner, owner)) return Fail("Only the arena's owner can change that.");
        if (a.Status != ArenaStatus.Active) return Fail($"'{arena}' is {a.Status.ToString().ToLowerInvariant()}.");
        Run("UPDATE arenas SET private = $p WHERE name = $a", ("$p", makePrivate ? 1 : 0), ("$a", arena));
        AddEvent(Now(), arena, owner, makePrivate ? "private" : "public", "");
        return Ok(makePrivate ? $"'{arena}' is private: left off the site's lists." : $"'{arena}' is public.");
    });

    /// <summary><paramref name="owner"/> discards <paramref name="arena"/>: archived, as by staff or the idle archive.</summary>
    public Result Discard(string arena, string owner) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null || !SameAccount(a.Owner, owner)) return Fail("Only the arena's owner can discard it.");
        if (a.Status == ArenaStatus.Archived) return Fail($"'{arena}' is already gone.");
        var now = Now();
        Run("UPDATE arenas SET status = 'Archived' WHERE name = $a", ("$a", arena));
        ArchiveLiveDraft(arena);
        AddPendingMove(arena, now);
        AddEvent(now, arena, owner, "discard", "by its owner");
        return Ok($"Discarded '{arena}'.");
    });
}

// Drafts are gone (Chris, 2026-11-25): anyone can enter any arena and an owner makes one private
// or public at will, so a draft is just a private arena. Leftover drafts become exactly that.
public sealed partial class ArenaStore
{
    /// <summary>
    /// Turns every leftover draft into a plain private arena of the same name and owner (its
    /// co-mods kept), once. <paramref name="moveFolder"/> moves <c>arenas/#name</c> to
    /// <c>arenas/name</c> inside the transaction (an error leaves that draft as it was). A draft
    /// whose name is taken by an arena (a published one's workshop copy) is just retired.
    /// Returns the names that became arenas.
    /// </summary>
    public IReadOnlyList<string> PromoteDrafts(Func<string, string?> moveFolder)
    {
        var promoted = new List<string>();
        var drafts = new List<(string Name, string Owner, string Created, string Touched)>();
        lock (_gate)
        {
            using var c = Cmd("SELECT name, owner, created, last_touched FROM drafts WHERE status <> 'Archived' ORDER BY name");
            using var r = c.ExecuteReader();
            while (r.Read()) drafts.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        foreach (var d in drafts)
        {
            Tx(() =>
            {
                var now = Now();
                Run("UPDATE requests SET status = 'Withdrawn', decided_by = $o WHERE arena = $a AND status = 'Pending' AND draft = 1",
                    ("$a", d.Name), ("$o", d.Owner));
                if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", d.Name)) > 0)
                {
                    Run("UPDATE drafts SET status = 'Archived' WHERE name = $a", ("$a", d.Name));
                    return Ok("");
                }
                var error = moveFolder(d.Name);
                if (error is not null) return Fail(error);
                Run("INSERT INTO arenas(name, owner, status, created, last_entered, private) VALUES($a, $o, 'Active', $c, $t, 1)",
                    ("$a", d.Name), ("$o", d.Owner), ("$c", d.Created), ("$t", d.Touched));
                Run("UPDATE comods SET arena = $a WHERE arena = $d AND player NOT IN (SELECT player FROM comods WHERE arena = $a)",
                    ("$a", d.Name), ("$d", DraftRules.DraftOf(d.Name)));
                Run("DELETE FROM comods WHERE arena = $d", ("$d", DraftRules.DraftOf(d.Name)));
                Run("DELETE FROM draft_invites WHERE name = $a", ("$a", d.Name));
                Run("UPDATE drafts SET status = 'Archived' WHERE name = $a", ("$a", d.Name));
                AddEvent(now, d.Name, "system", "private", "draft #" + d.Name + " became a private arena");
                promoted.Add(d.Name);
                return Ok("");
            });
        }
        return promoted;
    }
}
