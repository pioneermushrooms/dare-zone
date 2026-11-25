using System.Globalization;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// In-game commands. Each command needs the capability cmd_<name> in the player's group
// (see deploy/). Grouped by audience; staff overrides are in StaffCommands.cs.
public sealed partial class ArenaOwnership
{
    internal const int MaxPitchLength = 120;
    private const int PendingListMax = 10;
    private const int DecisionListMax = 5;
    private const int LogListMax = 10;

    private readonly List<(string Name, CommandDelegate Handler)> _commands = new();

    private void RegisterCommands()
    {
        // players
        Add("makearena", Command_requestarena);
        Add("requestarena", Command_requestarena); // the old name; same thing now
        Add("arenarequests", Command_arenarequests);
        Add("arenalog", Command_arenalog);
        Add("myarena", Command_myarena);

        // arena owners and co-mods (in their own arena; caps via arenaowner/arenamod)
        Add("arenaset", Command_arenaset);
        Add("arenaget", Command_arenaget);
        Add("comod", Command_comod);
        Add("arenamap", Command_arenamap); // not "putmap": the core AdminCommand owns ?putmap (see Uploads.cs)
        Add("putlvz", Command_putlvz);
        Add("rmlvz", Command_rmlvz);
        Add("weblogin", Command_weblogin);

        // drafts (any signed-in pilot)
        RegisterDraftCommands();

        // staff (mod and up)
        Add("approvearena", Command_approvearena);
        Add("declinearena", Command_declinearena);
        Add("createarena", Command_createarena);

        // staff overrides (smod and up: cmd_arenaadmin)
        Add("arenaadmin", Command_arenaadmin);
    }

    private void Add(string name, CommandDelegate handler)
    {
        _commandManager.AddCommand(name, handler);
        _commands.Add((name, handler));
    }

    private void UnregisterCommands()
    {
        foreach (var (name, handler) in _commands)
            _commandManager.RemoveCommand(name, handler);
        _commands.Clear();
    }

    private void Reply(Player player, string message) => _chat.SendMessage(player, message);

    /// <summary>Names with a pending request, longest-waiting first.</summary>
    private List<string> PendingNames() =>
        Store.PendingRequestsOldestFirst(200).Select(r => r.Arena).ToList();

    private static (string First, string Remainder) SplitFirst(ReadOnlySpan<char> parameters)
    {
        var s = parameters.Trim();
        var i = s.IndexOfAny(' ', '\t');
        return i < 0 ? (s.ToString(), "") : (s[..i].ToString(), s[(i + 1)..].Trim().ToString());
    }

    private static string When(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    // ---- players --------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<name> [what it's for]",
        Description = """
            Makes a new public arena that you own, right away (no approval), with its private
            draft #<name> to keep building in. Names are 3-15 letters and digits (a-z, 0-9),
            start with a letter and do not end in a digit. The optional line (up to 120
            characters) says what it's for; it is public. ?makearena and ?requestarena are the same.
            """)]
    private void Command_requestarena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        if (!player.Flags.Authenticated)
        {
            Reply(player, "Only players logged in with a registered (password-protected) name can make an arena.");
            return;
        }

        var (rawName, pitch) = SplitFirst(parameters);
        if (rawName.Length == 0)
        {
            Reply(player, "Usage: ?makearena <name> [what it's for]   e.g. ?makearena skyrace Fast racing laps for warbirds");
            return;
        }

        Reply(player, SubmitRequest(player.Name, rawName, pitch).Message);
    }

    /// <summary>
    /// The shared launch path for ?makearena/?requestarena and the web form (caller has checked the
    /// name is authenticated): pitch length, name rules and store limits, then the request is
    /// approved at once by the player themselves (<see cref="GoLive"/>). Mainloop.
    /// </summary>
    internal Result SubmitRequest(string playerName, string rawName, string pitch)
    {
        rawName = rawName.Trim();
        pitch = pitch.Trim();
        if (rawName.Length == 0)
            return new Result(false, "An arena name is required.");

        if (pitch.Length > MaxPitchLength)
            return new Result(false, $"Your pitch is {pitch.Length} characters; keep it to {MaxPitchLength} or fewer.");
        if (pitch.Any(char.IsControl))
            return new Result(false, "The pitch must be a single line of text.");

        if (!ArenaName.TryValidate(rawName, ExistingArenaFolders(), out var name, out var error))
            return new Result(false, $"Can't make '{rawName}': {error}");

        var result = Store.Request(playerName, name, pitch);
        if (result.Ok) return GoLive(playerName, name, pitch);
        // their own request from before arenas launched without approval: launch it now
        return OwnPendingRequest(playerName, name) ? GoLive(playerName, name, "") : result;
    }

    private bool OwnPendingRequest(string player, string name) =>
        Store.Requests(pendingOnly: true, 1000).Any(r =>
            string.Equals(r.Arena, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Player, player, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Arenas need no staff approval (Chris, 2026-11-25): the player's own pending request for
    /// <paramref name="name"/> (just made, by ?makearena, the web form or a draft's go-public) is
    /// approved by the player themselves through the normal approve path (folders, draft, recycle,
    /// public event). If that fails the request stays pending for staff. Mainloop.
    /// </summary>
    internal Result GoLive(string player, string name, string pitch)
    {
        var approved = ApproveArena(player, name);
        var line = pitch.Length > 0 ? ": " + pitch : "";
        if (!approved.Ok)
        {
            Log(LogLevel.Warn, $"{player}'s arena '{name}' could not go live ({approved.Message}); the request waits for staff.");
            _chat.SendModMessage($"[Dare.Arenas] {player}'s arena '{name}' could not go live: {approved.Message} (?approvearena {name} / ?declinearena {name} <reason>)");
            return new Result(false, $"Your arena '{name}' could not go live yet ({approved.Message}). It waits for staff now; see ?arenarequests.");
        }
        Log(LogLevel.Info, $"{player} launched arena '{name}'{line}");
        _chat.SendModMessage($"[Dare.Arenas] {player} launched arena '{name}'{line}");
        var message = $"'{name}' is live and yours. Type ?go {name}. Keep building in your draft #{name}; ?publish copies it to the live arena.";
        var recycleNote = approved.Message.IndexOf(" Could not recycle", StringComparison.Ordinal);
        if (recycleNote >= 0) message += approved.Message[recycleNote..];
        return new Result(true, message);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Lists the newest arenas, and any requests still waiting for staff (an arena that could not go live, or one from before arenas launched without approval).")]
    private void Command_arenarequests(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var pending = Store.PendingRequestsOldestFirst(PendingListMax);
        if (pending.Count > 0)
        {
            Reply(player, $"Pending arena requests, longest-waiting first ({pending.Count}):");
            foreach (var r in pending)
                Reply(player, $"  {r.Arena} - {r.Player} ({When(r.At)}): {r.Pitch}");
        }

        var decided = Store.Requests(pendingOnly: false, 50)
            .Where(r => r.Status != RequestStatus.Pending)
            .Take(DecisionListMax)
            .ToList();
        if (decided.Count == 0)
            Reply(player, "No arenas yet. Make one with ?makearena <name>.");
        else
        {
            Reply(player, "Newest arenas:");
            foreach (var r in decided)
            {
                if (r.Status == RequestStatus.Approved && string.Equals(r.DecidedBy, r.Player, StringComparison.OrdinalIgnoreCase))
                {
                    Reply(player, $"  {r.Arena} - launched by {r.Player} ({When(r.At)}){(string.IsNullOrEmpty(r.Pitch) ? "" : ": " + r.Pitch)}");
                    continue;
                }
                var verdict = r.Status switch
                {
                    RequestStatus.Approved => "approved",
                    RequestStatus.Withdrawn => "withdrawn",
                    _ => "declined",
                };
                var reason = string.IsNullOrEmpty(r.Reason) ? "" : $": {r.Reason}";
                Reply(player, $"  {r.Arena} - {r.Player}: {verdict} by {r.DecidedBy}{reason}");
            }
        }
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[name]",
        Description = "Shows the last 10 public arena events (requests, decisions, co-mod changes, ...), optionally for one arena.")]
    private void Command_arenalog(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var (name, _) = SplitFirst(parameters);
        string? arena = name.Length == 0 ? null : name.ToLowerInvariant();
        var events = Store.Events(arena, LogListMax);
        if (events.Count == 0)
        {
            Reply(player, arena is null ? "No arena events yet." : $"No events for '{arena}'.");
            return;
        }

        Reply(player, arena is null ? "Recent arena events:" : $"Recent events for '{arena}':");
        foreach (var e in events.Reverse())
        {
            var detail = string.IsNullOrEmpty(e.Detail) ? "" : $": {e.Detail}";
            Reply(player, $"  {When(e.At)} {e.Arena} {e.Kind} by {e.Actor}{detail}");
        }
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Shows the arena you own: status, co-mods and when someone last entered it.")]
    private void Command_myarena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        var owned = Store.OwnedArenas(player.Name);
        var pending = Store.Requests(pendingOnly: true, 1000)
            .Where(r => string.Equals(r.Player, player.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (owned.Count == 0 && pending.Count == 0)
        {
            Reply(player, "You don't own an arena. Build one with ?draft <name> (or at subspacefoundation.com/build.html).");
            return;
        }
        foreach (var a in owned)
        {
            Reply(player, $"Your arena: {a.Name} ({a.Status}){(Store.IsLiveWithDraft(a.Name) ? $"; build in #{a.Name}, then ?publish" : "")}");
            Reply(player, $"  Co-mods ({a.CoMods.Count}/{ArenaStore.MaxCoMods}): {(a.CoMods.Count == 0 ? "none" : string.Join(", ", a.CoMods))}");
            Reply(player, $"  Last entered: {When(a.LastEntered)}   Created: {When(a.Created)}");
        }
        foreach (var r in pending)
            Reply(player, $"Waiting for staff: '{r.Arena}' ({When(r.At)}). Take it back with ?withdrawrequest {r.Arena}.");
        Reply(player, "Your drafts: ?draft. The web page: ?weblogin or subspacefoundation.com/build.html.");
    }

    // ---- staff ----------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<name>",
        Description = """
            Approves a pending arena request: creates arenas/<name>/ from the template and makes
            the requester its owner (arena-scoped group arenaowner). The decision is public.
            """)]
    private void Command_approvearena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        var typed = parameters.ToString().Trim();
        if (typed.Length == 0)
        {
            Reply(player, "Usage: ?approvearena <name>   (see ?arenarequests)");
            return;
        }

        // Forgiving about spaces, quotes, capitals and the server's ' ' -> 'x' (see RequestNames).
        var pending = PendingNames();
        var name = RequestNames.Find(typed, pending);
        if (name is null)
        {
            Reply(player, RequestNames.NotFound(typed, pending));
            return;
        }
        Reply(player, ApproveArena(player.Name, name).Message);
    }

    /// <summary>
    /// Approves the pending request for <paramref name="name"/> (an exact pending name) for
    /// <paramref name="staff"/>: folders inside the approval, recycle, tell the requester. Shared by
    /// ?approvearena and the staff page; the caller has checked the capability. Mainloop.
    /// </summary>
    internal Result ApproveArena(string staff, string name)
    {
        // The folder is created inside the approval: if that fails, nothing is approved.
        var fromDraft = Store.IsDraftRequest(name);
        var result = Store.Approve(name, staff, ArenaFolderExists, () => CreateApprovalFolders(name));
        if (!result.Ok) return result;

        Log(LogLevel.Info, $"{staff} approved arena '{name}'.");
        var message = fromDraft
            ? $"{result.Message} Folder arenas/{name}/ copied from the draft #{name}."
            : $"{result.Message} Folder arenas/{name}/ created from the template, with its draft #{name}.";

        // Someone may already be in an arena of that name; it opened a shared fallback config
        // (no arenas/<name>/arena.conf then). Recycle it so it loads its own file, and players
        // re-enter (which also grants the owner). If it can't be recycled, staff must do it.
        var (recycled, notRecycled) = RecycleRunning(name);
        if (recycled.Count > 0)
            message += $" Recycled {string.Join(", ", recycled)} so it loads its new settings.";
        if (notRecycled.Count > 0)
        {
            message += $" Could not recycle {string.Join(", ", notRecycled)} (e.g. bots inside): go there and type ?recyclearena so it uses its own settings.";
            Log(LogLevel.Warn, $"Arena '{name}' approved while {string.Join(", ", notRecycled)} was running and could not be recycled.");
        }

        var owner = Store.Get(name)?.Owner;
        var requester = owner is null ? null : _playerData.FindPlayer(owner);
        if (requester is not null && !string.Equals(owner, staff, StringComparison.OrdinalIgnoreCase))
            Reply(requester, $"Your arena request '{name}' was approved by {staff}! Type ?go {name} to enter it, then ?myarena.");
        return new Result(true, message);
    }

    /// <summary>Recycles every running arena whose base name is <paramref name="name"/>.</summary>
    private (List<string> Recycled, List<string> NotRecycled) RecycleRunning(string name)
    {
        var running = new List<Arena>();
        _arenaManager.Lock();
        try
        {
            foreach (var a in _arenaManager.Arenas)
                if (string.Equals(a.BaseName, name, StringComparison.OrdinalIgnoreCase))
                    running.Add(a);
        }
        finally
        {
            _arenaManager.Unlock();
        }

        var recycled = new List<string>();
        var notRecycled = new List<string>();
        foreach (var a in running)
        {
            if (_arenaManager.RecycleArena(a))
            {
                recycled.Add(a.Name);
                Log(LogLevel.Info, $"Recycled [{a.Name}] after approving '{name}' so it loads arenas/{name}/{ArenaConfigFile.FileName}.");
            }
            else
            {
                notRecycled.Add(a.Name);
            }
        }
        return (recycled, notRecycled);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<name> <reason>",
        Description = "Declines a pending arena request. The reason is required and shown publicly in ?arenarequests.")]
    private void Command_declinearena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        var pending = PendingNames();
        var (name, reason) = RequestNames.SplitNameAndReason(parameters.ToString(), pending);
        if (name.Length == 0 || reason.Length == 0)
        {
            Reply(player, "Usage: ?declinearena <name> <reason>");
            return;
        }
        if (!pending.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Reply(player, RequestNames.NotFound(name, pending));
            return;
        }
        Reply(player, DeclineArena(player.Name, name, reason).Message);
    }

    /// <summary>Declines the pending request for <paramref name="name"/>; tells the requester. Shared with the staff page. Mainloop.</summary>
    internal Result DeclineArena(string staff, string name, string reason)
    {
        reason = reason.Trim();
        if (reason.Length == 0) return new Result(false, "A reason is required to decline a request.");
        if (reason.Length > MaxPitchLength || reason.Any(char.IsControl))
            return new Result(false, $"Keep the reason to one line of up to {MaxPitchLength} characters.");
        var requesterName = Store.Requests(pendingOnly: true, 1000)
            .FirstOrDefault(r => string.Equals(r.Arena, name, StringComparison.OrdinalIgnoreCase))?.Player;

        var result = Store.Decline(name, staff, reason);
        if (!result.Ok) return result;

        Log(LogLevel.Info, $"{staff} declined arena '{name}': {reason}");
        var requester = requesterName is null ? null : _playerData.FindPlayer(requesterName);
        if (requester is not null && !string.Equals(requesterName, staff, StringComparison.OrdinalIgnoreCase))
            Reply(requester, $"Your arena request '{name}' was declined by {staff}: {reason}");
        return result;
    }

    // ---- arena owners and co-mods -----------------------------------------------------

    /// <summary>Public base URL of the web pages (no trailing '/'); config DareArenas:WebBaseUrl.</summary>
    internal const string DefaultWebBaseUrl = "https://subspacefoundation.com/arena";
    private static readonly TimeSpan WebLoginInterval = TimeSpan.FromSeconds(30);

    /// <summary>Single-use web sign-in tokens; the web server (Task 7) redeems them.</summary>
    internal readonly WebAuth WebAuth = new(() => DateTimeOffset.UtcNow);

    // ?weblogin throttle: last link time per player (lowercased name); one link per 30 s.
    private readonly Dictionary<string, DateTimeOffset> _lastWebLogin = new();

    /// <summary>
    /// The gate for every owner/co-mod command: an authenticated name, standing in a player-owned
    /// arena where the store gives this player a role (owner only, if <paramref name="ownerOnly"/>).
    /// Replies with the reason on failure. <paramref name="arenaName"/> is the store's name.
    /// </summary>
    private bool TryArenaRole(Player player, bool ownerOnly, out Arena arena, out string arenaName, out string role)
    {
        arena = null!;
        arenaName = "";
        role = "";
        if (player.Name is null) return false;

        if (!player.Flags.Authenticated)
        {
            Reply(player, "Your name is not authenticated, so arena owner commands are not available. Log in with your password.");
            return false;
        }

        var current = player.Arena;
        var owned = current is null ? null : Store.OwnedOrDraft(current.BaseName.ToLowerInvariant());
        if (current is null || owned is null)
        {
            Reply(player, "Use this in your own arena (see ?myarena).");
            return false;
        }

        var r = Store.RoleIn(owned.Name, player.Name);
        if (r is null || (ownerOnly && r != "owner"))
        {
            Reply(player, ownerOnly ? "Only this arena's owner can do that." : "Only this arena's owner or co-mods can do that.");
            return false;
        }

        arena = current;
        arenaName = owned.Name;
        role = r;
        return true;
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<Section:Key> <value>",
        Description = """
            Changes one setting of your arena (owner or co-mod, in your arena), e.g.
            ?arenaset Warbird:BulletSpeed 3000. Only arena settings the server documents can be
            changed; Modules, General:Map and General:LevelFiles cannot. At most 20 changes per
            minute per arena.
            """)]
    private void Command_arenaset(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!TryArenaRole(player, ownerOnly: false, out _, out var arenaName, out _)) return;

        var (setting, value) = SplitFirst(parameters);
        if (setting.Length == 0 || value.Length == 0)
        {
            Reply(player, "Usage: ?arenaset <Section:Key> <value>   e.g. ?arenaset Warbird:BulletSpeed 3000");
            return;
        }

        var actor = player.Name!;
        WithOwnConfig(player, arenaName, ownerOnly: false, cfg => ApplySetting(cfg, arenaName, actor, setting, value));
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<Section:Key>",
        Description = "Shows the current value of one of your arena's settings (owner or co-mod, in your arena).")]
    private void Command_arenaget(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!TryArenaRole(player, ownerOnly: false, out _, out var arenaName, out _)) return;

        var (setting, _) = SplitFirst(parameters);
        if (setting.Length == 0)
        {
            Reply(player, "Usage: ?arenaget <Section:Key>   e.g. ?arenaget Warbird:BulletSpeed");
            return;
        }

        // the arena's own file, as the web page shows it (and as ?arenaset writes it)
        WithOwnConfig(player, arenaName, ownerOnly: false, cfg => ReadSetting(cfg, setting));
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[add|remove <player>]",
        Description = """
            Lists, adds or removes co-mods of your arena (owner only, in your arena; up to 5).
            Co-mods get moderator powers in this arena only. Changes are public (?arenalog).
            """)]
    private void Command_comod(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!TryArenaRole(player, ownerOnly: true, out var arena, out var arenaName, out _)) return;

        var (action, name) = SplitFirst(parameters);
        action = action.ToLowerInvariant();
        if (action.Length == 0)
        {
            var coMods = Store.OwnedOrDraft(arenaName)?.CoMods ?? Array.Empty<string>();
            Reply(player, $"Co-mods of {arenaName} ({coMods.Count}/{ArenaStore.MaxCoMods}): {(coMods.Count == 0 ? "none" : string.Join(", ", coMods))}");
            Reply(player, "Usage: ?comod add <player> / ?comod remove <player>");
            return;
        }
        if ((action != "add" && action != "remove") || name.Length == 0)
        {
            Reply(player, "Usage: ?comod add <player> / ?comod remove <player>");
            return;
        }
        if (name.Length > 24 || name.Any(char.IsControl))
        {
            Reply(player, "That is not a valid player name.");
            return;
        }

        Reply(player, ChangeCoMod(player.Name!, arenaName, action, name).Message);
    }

    /// <summary>
    /// The shared co-mod path for ?comod add/remove and the web hub. The caller checked that
    /// <paramref name="owner"/> is authenticated and owns <paramref name="arenaName"/> (the store
    /// checks ownership again). A new co-mod must be a claimed name. Mainloop.
    /// </summary>
    internal Result ChangeCoMod(string owner, string arenaName, string action, string name)
    {
        name = name.Trim();
        if ((action != "add" && action != "remove") || name.Length == 0)
            return new Result(false, "Usage: ?comod add <player> / ?comod remove <player>");
        if (name.Length > 24 || name.Any(char.IsControl))
            return new Result(false, "That is not a valid player name.");

        var online = _playerData.FindPlayer(name);
        if (action == "add")
        {
            // Only a claimed name: otherwise whoever first claims it would get the role.
            var claimed = ResolveClaimedName(name, online);
            if (claimed is null)
                return new Result(false, $"{name} has not claimed that name, so it can't be a co-mod yet. They can claim it at subspacefoundation.com.");
            name = claimed;
        }
        else
        {
            if (online?.Name is not null) name = online.Name; // canonical spelling
            else if (Store.OwnedOrDraft(arenaName)?.CoMods.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) is { } stored)
                name = stored;
        }
        if (online is not null && !string.Equals(online.Name, name, StringComparison.OrdinalIgnoreCase))
            online = null;

        var result = action == "add"
            ? Store.AddCoMod(arenaName, owner, name)
            : Store.RemoveCoMod(arenaName, owner, name);
        if (!result.Ok) return result;

        Log(LogLevel.Info, $"{owner} {(action == "add" ? "added" : "removed")} co-mod {name} in [{arenaName}].");
        if (online is null) return result;

        var inArena = online.Arena is not null && string.Equals(online.Arena.BaseName, arenaName, StringComparison.OrdinalIgnoreCase);
        if (action == "add")
        {
            Reply(online, $"{owner} made you a co-mod of arena '{arenaName}'.");
            if (inArena) ApplyGrant(online, online.Arena!);
        }
        else
        {
            Reply(online, $"{owner} removed you as a co-mod of arena '{arenaName}'.");
            if (inArena && string.Equals(_groupManager.GetGroup(online), GrantLogic.CoModGroup, StringComparison.OrdinalIgnoreCase))
                Revoke(online, online.Arena!, $"co-mod removed by {owner}");
        }
        return result;
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<client file.lvl>",
        Description = """
            Uploads a map from your Continuum folder to your arena (owner only, in your arena).
            It is stored as <arena>.lvl (2 MB max) and becomes General:Map. Other clients: use
            the web page (?weblogin).
            """)]
    private void Command_arenamap(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target) =>
        StartUpload(player, UploadKind.Map, parameters);

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<client file.lvz>",
        Description = """
            Uploads a graphics (.lvz) file from your Continuum folder to your arena (owner only, in
            your arena; 5 MB max, up to 10 files). It is added to General:LevelFiles; a file with
            the same name replaces the old one. Remove one with ?rmlvz. Other clients: use the
            web page (?weblogin).
            """)]
    private void Command_putlvz(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target) =>
        StartUpload(player, UploadKind.Lvz, parameters);

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<file.lvz>",
        Description = """
            Removes a graphics (.lvz) file from your arena (owner only, in your arena): it is taken
            out of General:LevelFiles and deleted. To replace a file, upload one with the same name.
            """)]
    private void Command_rmlvz(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!TryArenaRole(player, ownerOnly: true, out _, out var arenaName, out _)) return;

        var fileName = parameters.Trim().ToString();
        if (fileName.Length == 0)
        {
            Reply(player, "Usage: ?rmlvz <file.lvz>   (see ?arenaget General:LevelFiles)");
            return;
        }

        var actor = player.Name!;
        WithOwnConfig(player, arenaName, ownerOnly: true, cfg => RemoveLvz(cfg, arenaName, actor, fileName));
    }

    private void StartUpload(Player player, UploadKind kind, ReadOnlySpan<char> parameters)
    {
        if (!TryArenaRole(player, ownerOnly: true, out _, out var arenaName, out _)) return;

        var usage = kind == UploadKind.Map ? "Usage: ?arenamap <file.lvl>" : "Usage: ?putlvz <file.lvz>";
        if (!player.IsStandard)
        {
            Reply(player, "Your client can't send files; use the web page instead (?weblogin).");
            return;
        }

        var clientPath = parameters.Trim().ToString();
        if (clientPath.Length == 0)
        {
            Reply(player, usage);
            return;
        }

        // The client path may include the client's own folders; the server only uses the last part.
        var cut = clientPath.LastIndexOfAny(new[] { '/', '\\' });
        var fileName = clientPath[(cut + 1)..];
        var ext = kind == UploadKind.Map ? ".lvl" : ".lvz";
        if (!fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        {
            Reply(player, $"{usage}  (the file must end in {ext})");
            return;
        }
        if (kind == UploadKind.Lvz && !LevelFiles.IsSafeFileName(fileName))
        {
            Reply(player, $"Graphics file names may use only letters, digits, '.', '_' and '-' (up to {LevelFiles.MaxFileNameLength} characters).");
            return;
        }

        Reply(player, $"Requesting {fileName} from your client...");
        RequestUpload(player, arenaName, kind, clientPath, fileName);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = """
            Sends you a one-time sign-in link for the arena web page (valid 10 minutes, one use).
            Owners and co-mods edit their arena there; any logged-in player can request an arena.
            """)]
    private void Command_weblogin(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        // Any authenticated name may sign in (the web request form needs it); the web pages check
        // the arena role on every write. Unauthenticated names could be anyone's, so never.
        if (!player.Flags.Authenticated)
        {
            Reply(player, "Only players logged in with a registered (password-protected) name can sign in to the web page.");
            return;
        }

        var key = player.Name.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in _lastWebLogin.Where(p => now - p.Value >= WebLoginInterval).Select(p => p.Key).ToList())
            _lastWebLogin.Remove(stale);
        if (_lastWebLogin.TryGetValue(key, out var last))
        {
            var wait = (int)Math.Ceiling((WebLoginInterval - (now - last)).TotalSeconds);
            Reply(player, $"A sign-in link was just sent; wait {wait} s before asking for another.");
            return;
        }
        _lastWebLogin[key] = now;

        var roles = Store.RolesFor(player.Name);
        var token = WebAuth.IssueLoginToken(player.Name);
        _chat.SendMessage(player, $"Sign in to the arena web page: {WebBaseUrl}/login?t={token}");
        _chat.SendMessage(player, "The link is valid 10 minutes, one use. Don't share it.");
        Log(LogLevel.Info, $"Issued a web sign-in link to {player.Name} ({(roles.Count == 0 ? "no arena role" : string.Join(", ", roles.Select(r => $"{r.Role} of {r.Arena}")))}).");
    }
}
