namespace Dare.Arenas.Core;

/// <summary>
/// When a pilot deletes one of their names, everything that name has in the arena system goes
/// first, so whoever claims the freed name later inherits nothing.
/// </summary>
public sealed partial class ArenaStore
{
    /// <summary>Owner of an arena or draft whose pilot name was deleted: no pilot name can look like this.</summary>
    public static string DeletedOwner(string name) => "~deleted:" + name;

    /// <summary>What a name owns: public arenas (not archived) and private drafts (not archived, not public).</summary>
    public (IReadOnlyList<string> Arenas, IReadOnlyList<string> Drafts) Holdings(string player)
    {
        var arenas = new List<string>();
        var drafts = new List<string>();
        Tx(() =>
        {
            using (var c = Cmd("SELECT name FROM arenas WHERE owner = $p AND status <> 'Archived' ORDER BY name", ("$p", player)))
            using (var r = c.ExecuteReader())
                while (r.Read()) arenas.Add(r.GetString(0));
            using (var c = Cmd("SELECT name FROM drafts WHERE owner = $p AND status <> 'Archived' AND live = 0 ORDER BY name", ("$p", player)))
            using (var r = c.ExecuteReader())
                while (r.Read()) drafts.Add(r.GetString(0));
            return Ok("");
        });
        return (arenas, drafts);
    }

    /// <summary>
    /// Releases everything <paramref name="player"/> has: each public arena and private draft goes
    /// to the name <paramref name="keepWith"/> gives for it (another of the same pilot's names; the
    /// usual limits apply), or is archived under <see cref="DeletedOwner"/>. Pending requests are
    /// withdrawn; co-mod places and draft invites go. All or nothing. Returns the archived names
    /// (drafts as <c>#name</c>) so the caller can send players out of them.
    /// </summary>
    public Result ReleasePilot(string player, string actor, IReadOnlyDictionary<string, string> keepWith, out IReadOnlyList<string> archived)
    {
        var gone = new List<string>();
        var (arenas, drafts) = Holdings(player);
        var keep = new Dictionary<string, string>(keepWith, StringComparer.OrdinalIgnoreCase);
        var res = Tx(() =>
        {
            var now = Now();
            var tombstone = DeletedOwner(player);
            foreach (var name in arenas.Concat(drafts))
            {
                var isArena = arenas.Contains(name);
                if (keep.TryGetValue(name, out var to) && !string.IsNullOrWhiteSpace(to))
                {
                    if (Same(to, player)) return Fail($"'{name}' can't stay with the name being deleted.");
                    if (isArena && OwnedCount(to) >= MaxPerPilot) return Fail($"{to} already owns {MaxPerPilot} arenas; archive '{name}' or keep it with another name.");
                    if (DraftCount(to) >= MaxPerPilot && LoadDraft(name) is { Status: not DraftStatus.Archived })
                        return Fail($"{to} already has {MaxPerPilot} drafts; archive '{name}' or keep it with another name.");
                    if (isArena)
                    {
                        Run("DELETE FROM comods WHERE arena IN ($a, $d) AND player = $o", ("$a", name), ("$d", DraftRules.DraftOf(name)), ("$o", to));
                        Run("UPDATE arenas SET owner = $o WHERE name = $a", ("$o", to), ("$a", name));
                    }
                    Run("UPDATE drafts SET owner = $o WHERE name = $a", ("$o", to), ("$a", name));
                    Run("DELETE FROM draft_invites WHERE name = $a", ("$a", name));
                    AddEvent(now, name, actor, "transfer", $"{player} -> {to} (name deleted)");
                    continue;
                }
                if (isArena)
                {
                    Run("UPDATE arenas SET status = 'Archived', owner = $t WHERE name = $a", ("$t", tombstone), ("$a", name));
                    AddPendingMove(name, now);
                    gone.Add(name);
                }
                if (LoadDraft(name) is { Status: not DraftStatus.Archived })
                {
                    Run("UPDATE drafts SET status = 'Archived', owner = $t WHERE name = $a", ("$t", tombstone), ("$a", name));
                    AddPendingMove(DraftRules.DraftOf(name), now);
                    gone.Add(DraftRules.DraftOf(name));
                }
                else
                {
                    Run("UPDATE drafts SET owner = $t WHERE name = $a", ("$t", tombstone), ("$a", name));
                }
                AddEvent(now, name, actor, "archive", $"{player} deleted their name");
            }
            // archived arenas and drafts the name still owned: nobody inherits them either
            Run("UPDATE arenas SET owner = $t WHERE owner = $p", ("$t", tombstone), ("$p", player));
            Run("UPDATE drafts SET owner = $t WHERE owner = $p", ("$t", tombstone), ("$p", player));
            Run("UPDATE requests SET status = 'Withdrawn', decided_by = $p WHERE player = $p AND status = 'Pending'", ("$p", player));
            Run("DELETE FROM comods WHERE player = $p", ("$p", player));
            Run("DELETE FROM draft_invites WHERE player = $p", ("$p", player));
            return Ok("");
        });
        archived = res.Ok ? gone : Array.Empty<string>();
        return res;
    }
}
