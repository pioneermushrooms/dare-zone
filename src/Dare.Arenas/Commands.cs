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
        Add("requestarena", Command_requestarena);
        Add("arenarequests", Command_arenarequests);
        Add("arenalog", Command_arenalog);
        Add("myarena", Command_myarena);

        // arena owners and co-mods (in their own arena; caps via arenaowner/arenamod)
        Add("arenaset", Command_arenaset);
        Add("arenaget", Command_arenaget);
        Add("comod", Command_comod);
        Add("arenamap", Command_arenamap); // not "putmap": the core AdminCommand owns ?putmap (see Uploads.cs)
        Add("putlvz", Command_putlvz);
        Add("weblogin", Command_weblogin);

        // staff (mod and up)
        Add("approvearena", Command_approvearena);
        Add("declinearena", Command_declinearena);

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
        Args = "<name> <pitch>",
        Description = """
            Requests a new arena that you will own. Names are 3-16 characters (a-z, 0-9, -),
            start with a letter and do not end in a digit. The pitch (up to 120 characters) says
            what the arena is for. Staff decide publicly; see ?arenarequests.
            """)]
    private void Command_requestarena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        if (!player.Flags.Authenticated)
        {
            Reply(player, "Only players logged in with a registered (password-protected) name can request an arena.");
            return;
        }

        var (rawName, pitch) = SplitFirst(parameters);
        if (rawName.Length == 0 || pitch.Length == 0)
        {
            Reply(player, "Usage: ?requestarena <name> <pitch>   e.g. ?requestarena skyrace Fast racing laps for warbirds");
            return;
        }

        var result = SubmitRequest(player.Name, rawName, pitch);
        Reply(player, result.Ok
            ? $"{result.Message} Staff will review it publicly; check ?arenarequests."
            : result.Message);
    }

    /// <summary>
    /// The shared request path for ?requestarena and the web form (caller has checked the name is
    /// authenticated): pitch length, name rules and store limits; logs and notifies staff. Mainloop.
    /// </summary>
    internal Result SubmitRequest(string playerName, string rawName, string pitch)
    {
        rawName = rawName.Trim();
        pitch = pitch.Trim();
        if (rawName.Length == 0 || pitch.Length == 0)
            return new Result(false, "An arena name and a pitch are both required.");

        if (pitch.Length > MaxPitchLength)
            return new Result(false, $"Your pitch is {pitch.Length} characters; keep it to {MaxPitchLength} or fewer.");
        if (pitch.Any(char.IsControl))
            return new Result(false, "The pitch must be a single line of text.");

        if (!ArenaName.TryValidate(rawName, ExistingArenaFolders(), out var name, out var error))
            return new Result(false, $"Can't request '{rawName}': {error}");

        var result = Store.Request(playerName, name, pitch);
        if (result.Ok)
        {
            Log(LogLevel.Info, $"{playerName} requested arena '{name}': {pitch}");
            _chat.SendModMessage($"[Dare.Arenas] {playerName} requested arena '{name}': {pitch}  (?approvearena {name} / ?declinearena {name} <reason>)");
        }
        return result;
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = null,
        Description = "Lists pending arena requests and the most recent decisions.")]
    private void Command_arenarequests(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var pending = Store.Requests(pendingOnly: true, PendingListMax);
        if (pending.Count == 0)
            Reply(player, "No pending arena requests.");
        else
        {
            Reply(player, $"Pending arena requests ({pending.Count}):");
            foreach (var r in pending.Reverse())
                Reply(player, $"  {r.Arena} - {r.Player} ({When(r.At)}): {r.Pitch}");
        }

        var decided = Store.Requests(pendingOnly: false, 50)
            .Where(r => r.Status != RequestStatus.Pending)
            .Take(DecisionListMax)
            .ToList();
        if (decided.Count > 0)
        {
            Reply(player, "Recent decisions:");
            foreach (var r in decided)
            {
                var verdict = r.Status == RequestStatus.Approved ? "approved" : "declined";
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

        var owned = Store.OwnedBy(player.Name);
        if (owned is null)
        {
            var pending = Store.Requests(pendingOnly: true, 1000)
                .FirstOrDefault(r => string.Equals(r.Player, player.Name, StringComparison.OrdinalIgnoreCase));
            Reply(player, pending is not null
                ? $"Your request for '{pending.Arena}' is pending ({When(pending.At)}). See ?arenarequests."
                : "You don't own an arena. Request one with ?requestarena <name> <pitch>.");
            return;
        }

        Reply(player, $"Your arena: {owned.Name} ({owned.Status})");
        Reply(player, $"  Co-mods ({owned.CoMods.Count}/{ArenaStore.MaxCoMods}): {(owned.CoMods.Count == 0 ? "none" : string.Join(", ", owned.CoMods))}");
        Reply(player, $"  Last entered: {When(owned.LastEntered)}   Created: {When(owned.Created)}");
        Reply(player, "  Type ?weblogin for the web page");
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

        var (raw, _) = SplitFirst(parameters);
        if (raw.Length == 0)
        {
            Reply(player, "Usage: ?approvearena <name>   (see ?arenarequests)");
            return;
        }

        var name = raw.ToLowerInvariant();
        var result = Store.Approve(name, player.Name, ArenaFolderExists);
        if (!result.Ok)
        {
            Reply(player, result.Message);
            return;
        }

        Log(LogLevel.Info, $"{player.Name} approved arena '{name}'.");
        try
        {
            CreateArenaFolder(name);
            Log(LogLevel.Info, $"Created folder {Path.Combine(ArenasDir, name)} from template.");
            Reply(player, $"{result.Message} Folder arenas/{name}/ created from the template.");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Arena '{name}' is APPROVED in the store but creating its folder failed: {ex.Message}");
            Reply(player, $"{result.Message} BUT creating arenas/{name}/ failed: {ex.Message}. "
                          + "The approval is recorded; create the folder by hand (copy arenas/_template).");
        }

        var owner = Store.Get(name)?.Owner;
        var requester = owner is null ? null : _playerData.FindPlayer(owner);
        if (requester is not null)
        {
            Reply(requester, $"Your arena request '{name}' was approved by {player.Name}! Type ?go {name} to enter it, then ?myarena.");
            if (requester.Arena is not null && string.Equals(requester.Arena.BaseName, name, StringComparison.OrdinalIgnoreCase))
                ApplyGrant(requester, requester.Arena);
        }
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<name> <reason>",
        Description = "Declines a pending arena request. The reason is required and shown publicly in ?arenarequests.")]
    private void Command_declinearena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        var (raw, reason) = SplitFirst(parameters);
        if (raw.Length == 0 || reason.Length == 0)
        {
            Reply(player, "Usage: ?declinearena <name> <reason>");
            return;
        }

        var name = raw.ToLowerInvariant();
        var requesterName = Store.Requests(pendingOnly: true, 1000)
            .FirstOrDefault(r => string.Equals(r.Arena, name, StringComparison.OrdinalIgnoreCase))?.Player;

        var result = Store.Decline(name, player.Name, reason);
        Reply(player, result.Message);
        if (!result.Ok) return;

        Log(LogLevel.Info, $"{player.Name} declined arena '{name}': {reason}");
        var requester = requesterName is null ? null : _playerData.FindPlayer(requesterName);
        if (requester is not null)
            Reply(requester, $"Your arena request '{name}' was declined by {player.Name}: {reason}");
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
        var owned = current is null ? null : Store.Get(current.BaseName.ToLowerInvariant());
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
        if (!TryArenaRole(player, ownerOnly: false, out var arena, out var arenaName, out _)) return;

        var (setting, value) = SplitFirst(parameters);
        if (setting.Length == 0 || value.Length == 0)
        {
            Reply(player, "Usage: ?arenaset <Section:Key> <value>   e.g. ?arenaset Warbird:BulletSpeed 3000");
            return;
        }
        if (arena.Cfg is null)
        {
            Reply(player, "This arena's settings are not loaded; try again in a moment.");
            return;
        }

        Reply(player, ApplySetting(arena.Cfg, arenaName, player.Name!, setting, value).Message);
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<Section:Key>",
        Description = "Shows the current value of one of your arena's settings (owner or co-mod, in your arena).")]
    private void Command_arenaget(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!TryArenaRole(player, ownerOnly: false, out var arena, out _, out _)) return;

        var (setting, _) = SplitFirst(parameters);
        if (setting.Length == 0)
        {
            Reply(player, "Usage: ?arenaget <Section:Key>   e.g. ?arenaget Warbird:BulletSpeed");
            return;
        }
        if (arena.Cfg is null)
        {
            Reply(player, "This arena's settings are not loaded; try again in a moment.");
            return;
        }

        Reply(player, ReadSetting(arena.Cfg, setting).Message);
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
            var coMods = Store.Get(arenaName)?.CoMods ?? Array.Empty<string>();
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

        var online = _playerData.FindPlayer(name);
        if (online?.Name is not null) name = online.Name; // canonical spelling

        var result = action == "add"
            ? Store.AddCoMod(arenaName, player.Name!, name)
            : Store.RemoveCoMod(arenaName, player.Name!, name);
        Reply(player, result.Message);
        if (!result.Ok) return;

        Log(LogLevel.Info, $"{player.Name} {(action == "add" ? "added" : "removed")} co-mod {name} in [{arenaName}].");
        if (online is null) return;

        var inArena = online.Arena is not null && string.Equals(online.Arena.BaseName, arenaName, StringComparison.OrdinalIgnoreCase);
        if (action == "add")
        {
            Reply(online, $"{player.Name} made you a co-mod of arena '{arenaName}'.");
            if (inArena) ApplyGrant(online, online.Arena!);
        }
        else
        {
            Reply(online, $"{player.Name} removed you as a co-mod of arena '{arenaName}'.");
            if (inArena && string.Equals(_groupManager.GetGroup(online), GrantLogic.CoModGroup, StringComparison.OrdinalIgnoreCase))
                Revoke(online, online.Arena!, $"co-mod removed by {player.Name}");
        }
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
            your arena; 5 MB max, up to 10 files). It is added to General:LevelFiles. Other
            clients: use the web page (?weblogin).
            """)]
    private void Command_putlvz(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target) =>
        StartUpload(player, UploadKind.Lvz, parameters);

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
