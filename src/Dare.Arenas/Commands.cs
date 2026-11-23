using System.Globalization;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// In-game commands. Each command needs the capability cmd_<name> in the player's group
// (see deploy/). Grouped by audience; later tasks add owner and staff-override sections.
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

        // staff (mod and up)
        Add("approvearena", Command_approvearena);
        Add("declinearena", Command_declinearena);
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

        if (pitch.Length > MaxPitchLength)
        {
            Reply(player, $"Your pitch is {pitch.Length} characters; keep it to {MaxPitchLength} or fewer.");
            return;
        }

        if (!ArenaName.TryValidate(rawName, ExistingArenaFolders(), out var name, out var error))
        {
            Reply(player, $"Can't request '{rawName}': {error}");
            return;
        }

        var result = Store.Request(player.Name, name, pitch);
        Reply(player, result.Ok
            ? $"{result.Message} Staff will review it publicly; check ?arenarequests."
            : result.Message);
        if (result.Ok)
        {
            Log(LogLevel.Info, $"{player.Name} requested arena '{name}': {pitch}");
            _chat.SendModMessage($"[Dare.Arenas] {player.Name} requested arena '{name}': {pitch}  (?approvearena {name} / ?declinearena {name} <reason>)");
        }
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
}
