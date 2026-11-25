using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Drafts: each pilot's private arena #name, made at once from the template, where they own
// everything. Staff approve its first publish (a request linked to the draft); after that the
// owner publishes updates themselves, and the live arena changes only through a publish.
// The in-game commands and the web API (CreateDraftFor, InviteFor, RequestPublicFor,
// PublishFor) share these methods. Mainloop.
public sealed partial class ArenaOwnership
{
    private const string PublicArena = "0";
    internal const string DraftEntryRefused = "That arena is a private draft.";

    private void RegisterDraftCommands()
    {
        Add("draft", Command_draft);
        Add("draftinvite", Command_draftinvite);
        Add("requestpublic", Command_requestpublic);
        Add("publish", Command_publish);
        Add("withdrawrequest", Command_withdrawrequest);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[name]",
        Description = "Takes back your pending arena request (a draft's request returns it to editing).")]
    private void Command_withdrawrequest(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null || !player.Flags.Authenticated) return;
        Reply(player, WithdrawFor(player.Name, parameters.ToString()).Message);
    }

    /// <summary>Withdraws the player's pending request: the named one, or their only one.</summary>
    internal Result WithdrawFor(string player, string arena)
    {
        arena = arena.Trim();
        if (arena.Length == 0)
        {
            var mine = Store.PendingRequestsOldestFirst(200).Where(r => string.Equals(r.Player, player, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) return new Result(false, "You have no pending request.");
            if (mine.Count > 1) return new Result(false, $"Which one? ?withdrawrequest <name> ({string.Join(", ", mine.Select(r => r.Arena))}).");
            arena = mine[0].Arena;
        }
        return Store.Withdraw(player, arena.ToLowerInvariant());
    }

    // ---- shared with the web ---------------------------------------------------------------

    internal Result CreateDraftFor(string player, string rawName)
    {
        if (!DraftRules.TryValidate(rawName, ExistingArenaFolders(), out var name, out var error))
            return new Result(false, $"Can't make a draft named '{rawName.Trim()}': {error}");
        var draft = DraftRules.DraftOf(name);
        var result = Store.CreateDraft(player, name, ArenaFolderExists,
            () => DraftFiles.CreateFromTemplate(ArenasDir, TemplateDir, draft));
        if (result.Ok) Log(LogLevel.Info, $"{player} created draft {draft}.");
        return result;
    }

    /// <summary>
    /// Which of <paramref name="owner"/>'s drafts a command means: the one named, else the one they
    /// stand in (the draft or its live arena), else their only one. Null with a reason otherwise.
    /// </summary>
    private DraftInfo? PickDraft(string owner, string? name, Player? inGame, out string? why)
    {
        why = null;
        var mine = Store.DraftsOwnedBy(owner);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var wanted = DraftRules.PublicOf(name.Trim());
            var d = mine.FirstOrDefault(x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (d is null) why = $"You have no draft named #{wanted}.";
            return d;
        }
        if (inGame?.Arena?.BaseName is { } here)
        {
            var d = mine.FirstOrDefault(x => string.Equals(x.Name, DraftRules.PublicOf(here), StringComparison.OrdinalIgnoreCase));
            if (d is not null) return d;
        }
        if (mine.Count == 1) return mine[0];
        why = mine.Count == 0
            ? "You have no draft; make one with ?draft <name>."
            : $"Which draft? Go into it (?go #name) first. Yours: {string.Join(", ", mine.Select(x => "#" + x.Name))}.";
        return null;
    }

    internal Result InviteFor(string owner, string? draftName, string player, bool add, Player? inGame = null)
    {
        var d = PickDraft(owner, draftName, inGame, out var why);
        if (d is null) return new Result(false, why!);
        player = player.Trim();
        var result = add
            ? Store.Invite(d.Name, owner, player, p => _accounts?.Find(p) is not null)
            : Store.Uninvite(d.Name, owner, player);
        if (result.Ok && !add) MoveOutIfNotAllowed(d.Name, player);
        return result;
    }

    internal Result RequestPublicFor(string owner, string? draftName, string pitch, Player? inGame = null)
    {
        var d = PickDraft(owner, draftName, inGame, out var why);
        if (d is null) return new Result(false, why!);
        pitch = pitch.Trim();
        if (pitch.Length == 0) return new Result(false, "Say in a line what your arena is for.");
        if (pitch.Length > MaxPitchLength)
            return new Result(false, $"Your pitch is {pitch.Length} characters; keep it to {MaxPitchLength} or fewer.");
        if (pitch.Any(char.IsControl)) return new Result(false, "The pitch must be a single line of text.");
        var result = Store.RequestPublic(owner, d.Name, pitch);
        if (result.Ok)
        {
            Log(LogLevel.Info, $"{owner} asked to make draft #{d.Name} public: {pitch}");
            _chat.SendModMessage($"[Dare.Arenas] {owner} asked to make draft #{d.Name} public: {pitch}  (fly in it: ?go #{d.Name}; ?approvearena {d.Name} / ?declinearena {d.Name} <reason>)");
        }
        return result;
    }

    internal Result PublishFor(string owner, string? draftName, Player? inGame = null)
    {
        var d = PickDraft(owner, draftName, inGame, out var why);
        if (d is null) return new Result(false, why!);
        if (!d.Live) return new Result(false, $"#{d.Name} isn't public yet; ask staff with ?requestpublic <pitch>.");
        var live = Store.Get(d.Name);
        if (live is null || live.Status != ArenaStatus.Active)
            return new Result(false, $"'{d.Name}' is {live?.Status.ToString().ToLowerInvariant() ?? "missing"}; staff must revive it before you can publish.");
        if (!Store.PublishAllowed(d.Name))
            return new Result(false, $"'{d.Name}' was published less than {(int)ArenaStore.PublishInterval.TotalMinutes} minutes ago; try again shortly.");

        var draft = DraftRules.DraftOf(d.Name);
        var files = DraftRules.PublishFilesFromConf(ReadOwnConf(draft));
        var error = DraftFiles.Publish(ArenasDir, draft, d.Name, files, DateTimeOffset.UtcNow);
        if (error is not null)
        {
            Log(LogLevel.Error, $"Publish of {draft} to {d.Name} failed: {error}");
            return new Result(false, "Publishing failed and the live arena is unchanged; staff can see the details in the log.");
        }
        var result = Store.Published(d.Name, owner);
        Log(LogLevel.Info, $"{owner} published {draft} to {d.Name} ({files.Count} files).");
        var (recycled, notRecycled) = RecycleRunning(d.Name);
        var note = recycled.Count > 0 ? " The live arena restarted with it."
            : notRecycled.Count > 0 ? " It applies when the arena restarts (?recyclearena there)." : "";
        return new Result(result.Ok, result.Message + note);
    }

    // ---- approval folders ------------------------------------------------------------------

    /// <summary>
    /// The folders an approval needs, inside its transaction: a draft-linked request copies the
    /// draft into the new public arena; an old-style request makes the public arena from the
    /// template and its draft from that. Null on success, else a reason (nothing left behind).
    /// </summary>
    private string? CreateApprovalFolders(string name)
    {
        var draft = DraftRules.DraftOf(name);
        if (Store.IsDraftRequest(name))
        {
            var error = DraftFiles.CopyArena(ArenasDir, draft, name, DraftRules.PublishFilesFromConf(ReadOwnConf(draft)));
            if (error is null && ArenaConfigFile.OwnPath(ArenasDir, name) is null)
                error = $"the draft has no {ArenaConfigFile.FileName}";
            if (error is not null)
            {
                RemoveFolder(name);
                Log(LogLevel.Error, $"Copying draft {draft} to '{name}' failed: {error}");
            }
            return error;
        }

        var made = TryCreateArenaFolder(name);
        if (made is not null || Directory.Exists(Path.Combine(ArenasDir, draft))) return made;
        var copy = DraftFiles.CopyArena(ArenasDir, name, draft, DraftRules.PublishFilesFromConf(ReadOwnConf(name)));
        if (copy is not null)
        {
            RemoveFolder(name);
            Log(LogLevel.Error, $"Making draft {draft} for '{name}' failed: {copy}");
        }
        return copy;
    }

    /// <summary>Once at load: every owned arena without a draft gets one, copied from its live files.</summary>
    private void BackfillDrafts()
    {
        try
        {
            var made = Store.BackfillDrafts(name =>
                Directory.Exists(Path.Combine(ArenasDir, DraftRules.DraftOf(name)))
                    ? null // a folder left from before: keep it
                    : DraftFiles.CopyArena(ArenasDir, name, DraftRules.DraftOf(name), DraftRules.PublishFilesFromConf(ReadOwnConf(name))));
            if (made.Count > 0)
                Log(LogLevel.Info, $"Made drafts for {made.Count} owned arena(s): {string.Join(", ", made.Select(DraftRules.DraftOf))}.");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Making drafts for owned arenas failed: {ex.Message}");
        }
    }

    // ---- entry -----------------------------------------------------------------------------

    /// <summary>
    /// A player entered a draft: only its owner, co-mods, invited pilots and global staff may
    /// stay (names count only when authenticated); anyone else goes to the public arena.
    /// Returns false when the player was sent away.
    /// </summary>
    private bool DraftEntryAllowed(Player player, Arena arena)
    {
        if (MayEnterDraft(player, arena.BaseName)) return true;
        ApplyGrant(player, arena); // no role here any more: drops a group left from before
        _chat.SendMessage(player, DraftEntryRefused);
        _arenaManager.SendToArena(player, PublicArena, 0, 0);
        return false;
    }

    /// <summary>The entry rule for a draft (names count only when authenticated). Also used before entry.</summary>
    private bool MayEnterDraft(Player player, string draft)
    {
        var name = player.Name;
        if (name is null) return false;
        var authenticated = player.Flags.Authenticated;
        return DraftRules.Entry(
            authenticated ? Store.RoleIn(draft, name) : null,
            authenticated && Store.IsInvited(draft, name),
            authenticated && GrantLogic.IsGlobalStaff(GlobalGroupOf(name))) == EntryDecision.Allow;
    }

    private void MoveOutIfNotAllowed(string name, string playerName)
    {
        var p = _playerData.FindPlayer(playerName);
        var draft = DraftRules.DraftOf(name);
        if (p?.Arena is { } a && string.Equals(a.BaseName, draft, StringComparison.OrdinalIgnoreCase))
            DraftEntryAllowed(p, a);
    }

    /// <summary>After a transfer: everyone in the draft gets the grant their new role calls for, or leaves.</summary>
    private void RefreshDraftAfterTransfer(string name)
    {
        foreach (var p in PlayersIn(DraftRules.DraftOf(name)))
            if (p.Arena is { } a && DraftEntryAllowed(p, a))
                ApplyGrant(p, a);
    }

    // ---- live-edit lock --------------------------------------------------------------------

    /// <summary>Null when <paramref name="arenaName"/> may be edited directly, else why not (it publishes from a draft).</summary>
    internal string? LiveEditRefusal(string arenaName)
    {
        if (Store.IsLiveWithDraft(arenaName))
            return $"'{arenaName}' changes only by publishing: edit your draft #{arenaName} (?go #{arenaName}), then ?publish.";
        if (DraftRules.IsDraft(arenaName)) Store.TouchDraft(arenaName); // an edit counts as activity
        return null;
    }

    // ---- commands --------------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[name]",
        Description = """
            ?draft <name> makes your private draft arena #<name> and takes you there: you own it,
            nobody else sees it, and only pilots you invite can enter. ?draft on its own shows your
            draft and what to do next.
            """)]
    private void Command_draft(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;
        if (!player.Flags.Authenticated)
        {
            Reply(player, "Only pilots logged in with their Foundation account can make a draft.");
            return;
        }
        var raw = parameters.ToString().Trim();
        if (raw.Length == 0)
        {
            ShowDraft(player);
            return;
        }
        var result = CreateDraftFor(player.Name, raw);
        Reply(player, result.Message);
        if (result.Ok && DraftRules.TryValidate(raw, new HashSet<string>(), out var made, out _))
        {
            Reply(player, $"Taking you there. Invite friends with ?draftinvite add <name>; when it's ready, ?requestpublic <what it's for>.");
            _arenaManager.SendToArena(player, DraftRules.DraftOf(made), 0, 0);
        }
    }

    private void ShowDraft(Player player)
    {
        var mine = Store.DraftsOwnedBy(player.Name!);
        if (mine.Count == 0)
        {
            Reply(player, $"You have no draft. Make one with ?draft <name> (up to {ArenaStore.MaxPerPilot}), or at subspacefoundation.com/build.html.");
            return;
        }
        foreach (var d in mine)
        {
            var state = d.Live ? $"public as '{d.Name}': change it here, then ?publish"
                : d.Status == DraftStatus.Requested ? "waiting for staff"
                : "private: ?requestpublic <what it's for> when it's ready";
            var invited = d.Invited.Count == 0 ? "nobody invited" : "invited: " + string.Join(", ", d.Invited);
            Reply(player, $"#{d.Name} ({state}; {invited}).");
        }
        if (mine.Count < ArenaStore.MaxPerPilot)
            Reply(player, $"You can make {ArenaStore.MaxPerPilot - mine.Count} more with ?draft <name>. Commands act on the draft you're in.");
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "add|remove <player>",
        Description = "Lets a pilot into your draft (up to 5), or takes that back.")]
    private void Command_draftinvite(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null || !player.Flags.Authenticated) return;
        var (action, who) = SplitFirst(parameters);
        action = action.ToLowerInvariant();
        if (action is not ("add" or "remove") || who.Length == 0)
        {
            Reply(player, "Usage: ?draftinvite add|remove <player>");
            return;
        }
        Reply(player, InviteFor(player.Name, null, who, action == "add", player).Message);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<pitch>",
        Description = "Asks staff to make your draft public. They can fly in it before they decide.")]
    private void Command_requestpublic(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null || !player.Flags.Authenticated) return;
        Reply(player, RequestPublicFor(player.Name, null, parameters.ToString(), player).Message);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Copies your draft over your public arena (settings, map and graphics) and restarts it.")]
    private void Command_publish(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null || !player.Flags.Authenticated) return;
        var name = parameters.ToString().Trim();
        Reply(player, PublishFor(player.Name, name.Length == 0 ? null : name, player).Message);
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>The text of <c>arenas/&lt;folder&gt;/arena.conf</c>, or "" when it has none.</summary>
    private static string ReadOwnConf(string folder)
    {
        var path = ArenaConfigFile.OwnPath(ArenasDir, folder);
        try { return path is null ? "" : File.ReadAllText(path); }
        catch (IOException) { return ""; }
    }

    private void RemoveFolder(string name)
    {
        var path = Path.Combine(ArenasDir, name);
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log(LogLevel.Error, $"Could not remove {path}: {ex.Message}"); }
    }
}
