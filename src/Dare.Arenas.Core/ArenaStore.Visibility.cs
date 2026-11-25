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
        if (a is null || !Same(a.Owner, owner)) return Fail("Only the arena's owner can change that.");
        if (a.Status != ArenaStatus.Active) return Fail($"'{arena}' is {a.Status.ToString().ToLowerInvariant()}.");
        Run("UPDATE arenas SET private = $p WHERE name = $a", ("$p", makePrivate ? 1 : 0), ("$a", arena));
        AddEvent(Now(), arena, owner, makePrivate ? "private" : "public", "");
        return Ok(makePrivate ? $"'{arena}' is private: left off the site's lists." : $"'{arena}' is public.");
    });

    /// <summary><paramref name="owner"/> discards <paramref name="arena"/>: archived, as by staff or the idle archive.</summary>
    public Result Discard(string arena, string owner) => Tx(() =>
    {
        var a = Load(arena);
        if (a is null || !Same(a.Owner, owner)) return Fail("Only the arena's owner can discard it.");
        if (a.Status == ArenaStatus.Archived) return Fail($"'{arena}' is already gone.");
        var now = Now();
        Run("UPDATE arenas SET status = 'Archived' WHERE name = $a", ("$a", arena));
        ArchiveLiveDraft(arena);
        AddPendingMove(arena, now);
        AddEvent(now, arena, owner, "discard", "by its owner");
        return Ok($"Discarded '{arena}'.");
    });
}
