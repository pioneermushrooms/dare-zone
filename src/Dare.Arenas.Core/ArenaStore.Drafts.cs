namespace Dare.Arenas.Core;

/// <summary>
/// Drafts: each pilot's private arena <c>#name</c>. Draft names are stored without the '#'
/// (table drafts); draft co-mods live in comods under the arena name <c>#name</c>, so the
/// existing co-mod and role paths work inside drafts. A draft and its public name are
/// reserved together. Events about the draft itself use the arena <c>#name</c>; events about
/// the public arena (request, approve, publish) use <c>name</c>.
/// </summary>
public sealed partial class ArenaStore
{
    public const int MaxInvites = 5;
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMinutes(2);

    // ---- mutations -------------------------------------------------------

    /// <summary>
    /// Creates the draft <c>#name</c> for <paramref name="owner"/>. <paramref name="name"/> is an
    /// already validated, lower-case public name (see <see cref="DraftRules.TryValidate"/>).
    /// <paramref name="createFolder"/> runs inside the transaction after every check passed; an
    /// error from it rolls everything back.
    /// </summary>
    public Result CreateDraft(string owner, string name, Func<string, bool> folderExists, Func<string?> createFolder) => Tx(() =>
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            return Fail("Player and draft name are required.");
        name = DraftRules.PublicOf(name);
        if (LoadDraftByOwner(owner) is { } mine)
            return Fail($"You already have a draft (#{mine.Name}).");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE owner = $p", ("$p", owner)) > 0)
            return Fail("You already own an arena; its draft is where you build.");
        if (Scalar("SELECT COUNT(*) FROM requests WHERE player = $p AND status = 'Pending'", ("$p", owner)) > 0)
            return Fail("You already have a pending arena request.");
        if (NameTaken(name))
            return Fail($"The name '{name}' is already taken.");
        var draft = DraftRules.DraftOf(name);
        if (folderExists(draft) || folderExists(name))
            return Fail($"A folder named '{name}' already exists on the server.");
        var folderError = createFolder();
        if (folderError is not null)
            return Fail($"Creating your draft failed ({folderError}); nothing was created.");
        var now = Now();
        Run("INSERT INTO drafts(name, owner, status, live, created, last_touched) VALUES($n,$o,'Active',0,$t,$t)",
            ("$n", name), ("$o", owner), ("$t", now));
        AddEvent(now, draft, owner, "draft", "created");
        return Ok($"Your draft {draft} is ready.");
    });

    public Result Invite(string name, string owner, string player, Func<string, bool> hasAccount) => Tx(() =>
    {
        var d = LoadDraft(name);
        if (d is null) return Fail($"No such draft '{DraftRules.DraftOf(name)}'.");
        if (!Same(d.Owner, owner)) return Fail("Only the draft's owner can invite pilots.");
        if (string.IsNullOrWhiteSpace(player)) return Fail("Player name is required.");
        if (Same(d.Owner, player)) return Fail("You don't need an invite to your own draft.");
        if (!hasAccount(player)) return Fail($"{player} has no Foundation account.");
        if (d.Invited.Any(i => Same(i, player))) return Fail($"{player} is already invited.");
        if (d.Invited.Count >= MaxInvites) return Fail($"A draft can have at most {MaxInvites} invited pilots.");
        Run("INSERT INTO draft_invites(name, player) VALUES($n,$p)", ("$n", d.Name), ("$p", player));
        AddEvent(Now(), DraftRules.DraftOf(d.Name), owner, "invite", player);
        return Ok($"{player} can now enter #{d.Name}.");
    });

    public Result Uninvite(string name, string owner, string player) => Tx(() =>
    {
        var d = LoadDraft(name);
        if (d is null) return Fail($"No such draft '{DraftRules.DraftOf(name)}'.");
        if (!Same(d.Owner, owner)) return Fail("Only the draft's owner can remove invites.");
        if (!d.Invited.Any(i => Same(i, player))) return Fail($"{player} is not invited.");
        Run("DELETE FROM draft_invites WHERE name=$n AND player=$p", ("$n", d.Name), ("$p", player));
        AddEvent(Now(), DraftRules.DraftOf(d.Name), owner, "uninvite", player);
        return Ok($"{player} can no longer enter #{d.Name}.");
    });

    /// <summary>Asks staff to make the owner's draft public (a request for its public name, linked to the draft).</summary>
    public Result RequestPublic(string owner, string pitch) => Tx(() =>
    {
        var d = LoadDraftByOwner(owner);
        if (d is null) return Fail("You have no draft; make one with ?draft <name>.");
        if (d.Live) return Fail($"#{d.Name} is already public; use ?publish to update it.");
        if (d.Status == DraftStatus.Requested) return Fail($"#{d.Name} is already waiting for staff.");
        if (d.Status != DraftStatus.Active) return Fail($"#{d.Name} is {d.Status.ToString().ToLowerInvariant()}.");
        if (Scalar("SELECT COUNT(*) FROM arenas WHERE name = $a", ("$a", d.Name)) > 0)
            return Fail($"Arena '{d.Name}' is already taken.");
        var now = Now();
        Run("INSERT INTO requests(arena, player, pitch, status, at, draft) VALUES($a,$p,$pitch,'Pending',$at,1)",
            ("$a", d.Name), ("$p", owner), ("$pitch", pitch ?? ""), ("$at", now));
        Run("UPDATE drafts SET status='Requested', last_touched=$t WHERE name=$n", ("$t", now), ("$n", d.Name));
        AddEvent(now, d.Name, owner, "request", $"from draft: {pitch}");
        return Ok($"Asked staff to make #{d.Name} public as '{d.Name}'.");
    });

    /// <summary>Records a publish of the draft into its live arena (the files were already copied).</summary>
    public Result Published(string name, string owner) => Tx(() =>
    {
        var d = LoadDraft(name);
        if (d is null || !d.Live) return Fail($"#{DraftRules.PublicOf(name)} is not public yet.");
        var now = Now();
        Run("UPDATE drafts SET last_touched=$t WHERE name=$n", ("$t", now), ("$n", d.Name));
        AddEvent(now, d.Name, owner, "publish", $"from #{d.Name}");
        return Ok($"Published #{d.Name} to {d.Name}.");
    });

    /// <summary>Records that staff put the previous published version of <paramref name="name"/> back.</summary>
    public Result RolledBack(string name, string staff) => Tx(() =>
    {
        var d = LoadDraft(name);
        if (d is null || !d.Live) return Fail($"'{DraftRules.PublicOf(name)}' is not published from a draft.");
        AddEvent(Now(), d.Name, staff, "rollback", "previous version restored");
        return Ok($"Rolled '{d.Name}' back to its previous version.");
    });

    public void TouchDraft(string name) => Tx(() =>
    {
        Run("UPDATE drafts SET last_touched=$t WHERE name=$n AND status <> 'Archived'",
            ("$t", Now()), ("$n", DraftRules.PublicOf(name)));
        return Ok("");
    });

    /// <summary>
    /// Archives every draft not edited or entered for <paramref name="idle"/>, except those
    /// <paramref name="occupied"/> returns true for (given <c>#name</c>). Each gets a pending
    /// folder move for <c>#name</c>. Returns the archived <c>#name</c>s.
    /// </summary>
    public IReadOnlyList<string> ArchiveIdleDrafts(TimeSpan idle, Func<string, bool> occupied)
    {
        var archived = new List<string>();
        Tx(() =>
        {
            var nowDto = _clock();
            var now = Fmt(nowDto);
            var names = new List<string>();
            using (var cmd = Cmd("SELECT name FROM drafts WHERE status <> 'Archived' AND last_touched < $c ORDER BY name",
                       ("$c", Fmt(nowDto - idle))))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) names.Add(r.GetString(0));
            foreach (var n in names)
            {
                var draft = DraftRules.DraftOf(n);
                if (occupied(draft)) continue;
                Run("UPDATE drafts SET status='Archived' WHERE name=$n", ("$n", n));
                AddPendingMove(draft, now);
                AddEvent(now, draft, "system", "archive", $"idle more than {(int)idle.TotalDays} days");
                archived.Add(draft);
            }
            return Ok("");
        });
        return archived;
    }

    /// <summary>Archived → Published (if live) or Active. <paramref name="ensureFolder"/> restores <c>arenas/#name/</c>.</summary>
    public Result ReviveDraft(string name, string staff, Func<string?> ensureFolder) => Tx(() =>
    {
        var d = LoadDraft(name);
        if (d is null) return Fail($"No such draft '{DraftRules.DraftOf(name)}'.");
        if (d.Status != DraftStatus.Archived) return Fail($"#{d.Name} is not archived.");
        if (LoadDraftByOwner(d.Owner) is { } other)
            return Fail($"{d.Owner} has another draft (#{other.Name}) now.");
        var folderError = ensureFolder();
        if (folderError is not null) return Fail($"Can't revive #{d.Name}: {folderError}");
        var draft = DraftRules.DraftOf(d.Name);
        var now = Now();
        Run("DELETE FROM pending_archive_moves WHERE arena=$a", ("$a", draft));
        Run("UPDATE drafts SET status=$s, last_touched=$t WHERE name=$n",
            ("$s", d.Live ? "Published" : "Active"), ("$t", now), ("$n", d.Name));
        AddEvent(now, draft, staff, "revive", "was Archived");
        return Ok($"Revived #{d.Name}.");
    });

    /// <summary>
    /// Gives every owned (not archived) arena without a draft its draft: Published and live,
    /// owned by the arena's owner, with the arena's co-mods. <paramref name="copyFiles"/> (given
    /// the public name) makes <c>arenas/#name/</c> from the live files; an error skips that arena.
    /// Returns the arenas that got a draft.
    /// </summary>
    public IReadOnlyList<string> BackfillDrafts(Func<string, string?> copyFiles)
    {
        var made = new List<string>();
        List<(string Name, string Owner)> arenas;
        lock (_gate)
        {
            arenas = new();
            using var cmd = Cmd("""
                SELECT a.name, a.owner FROM arenas a
                WHERE a.status <> 'Archived'
                  AND NOT EXISTS (SELECT 1 FROM drafts d WHERE d.name = a.name OR (d.owner = a.owner AND d.status <> 'Archived'))
                ORDER BY a.name
                """);
            using var r = cmd.ExecuteReader();
            while (r.Read()) arenas.Add((r.GetString(0), r.GetString(1)));
        }
        foreach (var (name, owner) in arenas)
        {
            var res = Tx(() =>
            {
                if (Scalar("SELECT COUNT(*) FROM drafts WHERE name = $n OR (owner = $o AND status <> 'Archived')", ("$n", name), ("$o", owner)) > 0)
                    return Fail("already has a draft");
                var error = copyFiles(name);
                if (error is not null) return Fail(error);
                var now = Now();
                var draft = DraftRules.DraftOf(name);
                Run("INSERT INTO drafts(name, owner, status, live, created, last_touched) VALUES($n,$o,'Published',1,$t,$t)",
                    ("$n", name), ("$o", owner), ("$t", now));
                Run("INSERT OR IGNORE INTO comods(arena, player) SELECT $d, player FROM comods WHERE arena = $n",
                    ("$d", draft), ("$n", name));
                AddEvent(now, draft, "system", "draft", $"made from the live arena {name}");
                return Ok("");
            });
            if (res.Ok) made.Add(name);
        }
        return made;
    }

    // ---- queries ---------------------------------------------------------

    /// <summary>The draft named <paramref name="name"/> (with or without '#').</summary>
    public DraftInfo? Draft(string name)
    {
        lock (_gate) return LoadDraft(name);
    }

    public DraftInfo? DraftOwnedBy(string player)
    {
        lock (_gate) return LoadDraftByOwner(player);
    }

    public bool IsInvited(string name, string player)
    {
        lock (_gate)
            return Scalar("SELECT COUNT(*) FROM draft_invites WHERE name = $n AND player = $p",
                ("$n", DraftRules.PublicOf(name)), ("$p", player)) > 0;
    }

    /// <summary>The newest request for <paramref name="arena"/> came from a draft.</summary>
    public bool IsDraftRequest(string arena)
    {
        lock (_gate)
            return Scalar("SELECT COALESCE((SELECT draft FROM requests WHERE arena = $a ORDER BY id DESC LIMIT 1), 0)",
                ("$a", arena)) == 1;
    }

    /// <summary>The public arena <paramref name="arena"/> has a live draft, so content changes go through the draft.</summary>
    public bool IsLiveWithDraft(string arena)
    {
        if (DraftRules.IsDraft(arena)) return false;
        lock (_gate)
            return Scalar("SELECT COUNT(*) FROM drafts WHERE name = $n AND live = 1", ("$n", arena)) > 0;
    }

    /// <summary>No publish of <paramref name="name"/> in the last <see cref="PublishInterval"/>.</summary>
    public bool PublishAllowed(string name)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT at FROM events WHERE arena = $a AND kind = 'publish' ORDER BY id DESC LIMIT 1",
                ("$a", DraftRules.PublicOf(name)));
            return cmd.ExecuteScalar() is not string last || _clock() - Parse(last) >= PublishInterval;
        }
    }

    // ---- internals -------------------------------------------------------

    private bool NameTaken(string name) =>
        Scalar("SELECT COUNT(*) FROM drafts WHERE name = $n", ("$n", name)) > 0
        || Scalar("SELECT COUNT(*) FROM arenas WHERE name = $n", ("$n", name)) > 0
        || Scalar("SELECT COUNT(*) FROM requests WHERE arena = $n AND status = 'Pending'", ("$n", name)) > 0;

    private bool IsDraftRequestId(long id) =>
        Scalar("SELECT COALESCE(draft, 0) FROM requests WHERE id = $id", ("$id", id)) == 1;

    private DraftInfo? LoadDraftByOwner(string player)
    {
        // One draft per pilot that isn't archived; an archived one keeps its name but frees the slot.
        using var cmd = Cmd("SELECT name FROM drafts WHERE owner = $p AND status <> 'Archived'", ("$p", player));
        return cmd.ExecuteScalar() is string name ? LoadDraft(name) : null;
    }

    private DraftInfo? LoadDraft(string name)
    {
        name = DraftRules.PublicOf(name);
        string n, owner, status, created, touched;
        long live;
        using (var cmd = Cmd("SELECT name, owner, status, live, created, last_touched FROM drafts WHERE name = $n", ("$n", name)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            n = r.GetString(0); owner = r.GetString(1); status = r.GetString(2);
            live = r.GetInt64(3); created = r.GetString(4); touched = r.GetString(5);
        }
        var comods = new List<string>();
        using (var cmd = Cmd("SELECT player FROM comods WHERE arena = $a ORDER BY rowid", ("$a", DraftRules.DraftOf(n))))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) comods.Add(r.GetString(0));
        var invited = new List<string>();
        using (var cmd = Cmd("SELECT player FROM draft_invites WHERE name = $n ORDER BY rowid", ("$n", n)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) invited.Add(r.GetString(0));
        return new DraftInfo(n, owner, Enum.Parse<DraftStatus>(status), live == 1, Parse(created), Parse(touched),
            comods, invited);
    }

    /// <summary>The draft as an owned arena named <c>#name</c> (Active unless archived), for the shared role and co-mod code.</summary>
    private OwnedArena? LoadDraftAsArena(string arena)
    {
        var d = LoadDraft(arena);
        return d is null
            ? null
            : new OwnedArena(DraftRules.DraftOf(d.Name), d.Owner,
                d.Status == DraftStatus.Archived ? ArenaStatus.Archived : ArenaStatus.Active,
                d.Created, d.LastTouched, d.CoMods);
    }

    private OwnedArena? DraftAsArena(string arena)
    {
        lock (_gate) return LoadDraftAsArena(arena);
    }
}
