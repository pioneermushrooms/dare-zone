using System.Text;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Staff overrides (?arenaadmin, smod and up via cmd_arenaadmin), the daily idle-archive check, and
// keeping players out of suspended/archived arenas. Every decision appends a public event (store).
public sealed partial class ArenaOwnership : IArenaAuthorizationAdvisor
{
    internal const string ArchiveDir = "_archive";
    internal const string AdminCapability = "cmd_arenaadmin";
    internal static readonly TimeSpan IdleBeforeArchive = TimeSpan.FromDays(30);
    private const int ArchiveCheckIntervalMs = 24 * 60 * 60 * 1000;
    private const int ArchiveFirstCheckMs = 5 * 60 * 1000;
    private const int MaxReasonLength = 120;

    private AdvisorRegistrationToken<IArenaAuthorizationAdvisor>? _authAdvisorToken;

    private void RegisterStaff(IComponentBroker broker)
    {
        _authAdvisorToken = broker.RegisterAdvisor<IArenaAuthorizationAdvisor>(this);
        _mainloopTimer.SetTimer(Timer_ArchiveIdle, ArchiveFirstCheckMs, ArchiveCheckIntervalMs, null);
    }

    private void UnregisterStaff(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(Timer_ArchiveIdle, null);
        broker.UnregisterAdvisor(ref _authAdvisorToken);
    }

    // ---- ?arenaadmin --------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "suspend <name> <reason> | revive <name> | transfer <name> <player> | archive <name>",
        Description = """
            Staff overrides for player-owned arenas; each is public in ?arenalog.
            suspend: closes the arena to players (reason required). revive: reopens a suspended or
            archived arena (an archived folder is moved back from arenas/_archive/). transfer: gives
            the arena to another player. archive: closes it and moves its folder to arenas/_archive/.
            """)]
    private void Command_arenaadmin(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        const string usage = "Usage: ?arenaadmin suspend <name> <reason> | revive <name> | transfer <name> <player> | archive <name>";
        var (action, rest) = SplitFirst(parameters);
        var (rawName, arg) = SplitFirst(rest);
        action = action.ToLowerInvariant();
        if (action.Length == 0 || rawName.Length == 0)
        {
            Reply(player, usage);
            return;
        }

        var name = rawName.ToLowerInvariant();
        var owned = Store.Get(name);
        if (owned is null)
        {
            Reply(player, $"'{name}' is not a player-owned arena.");
            return;
        }
        name = owned.Name; // the store's (validated) name from here on

        switch (action)
        {
            case "suspend":
                if (arg.Length == 0 || arg.Length > MaxReasonLength || arg.Any(char.IsControl))
                {
                    Reply(player, $"Usage: ?arenaadmin suspend <name> <reason>   (reason required, up to {MaxReasonLength} characters)");
                    return;
                }
                AdminSuspend(player, name, arg);
                break;

            case "revive":
                AdminRevive(player, name, owned.Status);
                break;

            case "transfer":
                if (arg.Length == 0 || arg.Length > 24 || arg.Any(char.IsControl))
                {
                    Reply(player, "Usage: ?arenaadmin transfer <name> <player>");
                    return;
                }
                AdminTransfer(player, name, owned.Owner, arg);
                break;

            case "archive":
                AdminArchive(player, name);
                break;

            default:
                Reply(player, usage);
                break;
        }
    }

    private void AdminSuspend(Player staff, string name, string reason)
    {
        var result = Store.Suspend(name, staff.Name!, reason);
        Reply(staff, result.Message);
        if (!result.Ok) return;

        Log(LogLevel.Info, $"{staff.Name} suspended [{name}]: {reason}");
        var moved = SendPlayersOut(name, $"Arena '{name}' was suspended by staff: {reason}");
        if (moved > 0) Reply(staff, $"{moved} player(s) were moved out of '{name}'.");
    }

    private void AdminArchive(Player staff, string name)
    {
        var result = Store.Archive(name, staff.Name!);
        if (!result.Ok)
        {
            Reply(staff, result.Message);
            return;
        }

        Log(LogLevel.Info, $"{staff.Name} archived [{name}].");
        var (moved, folder) = CloseArchived(name, $"Arena '{name}' was archived by staff.");
        Reply(staff, folder is null
            ? $"{result.Message} Its folder could not be moved; see the log."
            : $"{result.Message} Folder moved to arenas/{ArchiveDir}/{folder}/.{(moved > 0 ? $" {moved} player(s) moved out." : "")}");
    }

    private void AdminRevive(Player staff, string name, ArenaStatus wasStatus)
    {
        var result = Store.Revive(name, staff.Name!);
        if (!result.Ok)
        {
            Reply(staff, result.Message);
            return;
        }

        Log(LogLevel.Info, $"{staff.Name} revived [{name}] (was {wasStatus}).");
        if (wasStatus == ArenaStatus.Archived && !ArenaFolderExists(name))
        {
            var restored = RestoreFromArchive(name);
            Reply(staff, restored is null
                ? $"{result.Message} BUT no archived folder for it was found in arenas/{ArchiveDir}/; restore or create arenas/{name}/ by hand."
                : $"{result.Message} Folder arenas/{ArchiveDir}/{restored}/ moved back to arenas/{name}/.");
        }
        else
        {
            Reply(staff, result.Message);
        }

        var owner = Store.Get(name)?.Owner;
        var online = owner is null ? null : _playerData.FindPlayer(owner);
        if (online is not null)
            Reply(online, $"Your arena '{name}' was reopened by {staff.Name}. Type ?go {name}.");
    }

    private void AdminTransfer(Player staff, string name, string oldOwner, string newOwnerRaw)
    {
        var online = _playerData.FindPlayer(newOwnerRaw);
        var newOwner = online?.Name ?? newOwnerRaw; // canonical spelling when online

        var result = Store.Transfer(name, staff.Name!, newOwner);
        Reply(staff, result.Message);
        if (!result.Ok) return;

        Log(LogLevel.Info, $"{staff.Name} transferred [{name}] from {oldOwner} to {newOwner}.");
        var old = _playerData.FindPlayer(oldOwner);
        if (old is not null)
            Reply(old, $"Staff ({staff.Name}) transferred your arena '{name}' to {newOwner}.");
        if (online is not null)
            Reply(online, $"Staff ({staff.Name}) made you the owner of arena '{name}'. Type ?go {name}, then ?myarena.");

        RefreshGrantsIn(name);
    }

    // ---- daily idle archive ---------------------------------------------------------------

    private bool Timer_ArchiveIdle()
    {
        if (_store is null) return false;
        try
        {
            var archived = Store.ArchiveIdle(IdleBeforeArchive);
            foreach (var name in archived)
            {
                var (_, folder) = CloseArchived(name, $"Arena '{name}' was archived after {(int)IdleBeforeArchive.TotalDays} days without visitors.");
                Log(LogLevel.Info, folder is null
                    ? $"Archived idle arena [{name}] (no entry for {(int)IdleBeforeArchive.TotalDays} days); its folder could not be moved."
                    : $"Archived idle arena [{name}] (no entry for {(int)IdleBeforeArchive.TotalDays} days) to arenas/{ArchiveDir}/{folder}/.");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Daily archive check failed: {ex.Message}");
        }
        return true; // keep the timer
    }

    // ---- folders --------------------------------------------------------------------------

    /// <summary>
    /// The shared follow-up for an arena the store has just archived (staff command and daily check):
    /// moves non-staff players out, refreshes grants, then moves the folder to the archive.
    /// </summary>
    private (int Moved, string? Folder) CloseArchived(string name, string message)
    {
        var moved = SendPlayersOut(name, message);
        return (moved, MoveToArchive(name));
    }

    /// <summary>
    /// Moves <c>arenas/&lt;name&gt;/</c> to <c>arenas/_archive/&lt;name&gt;[-n]/</c>, never overwriting an
    /// earlier archive. Returns the archive folder name, or null (logged) when it could not be moved.
    /// <paramref name="name"/> must come from the store.
    /// </summary>
    private string? MoveToArchive(string name)
    {
        try
        {
            var source = Path.GetFullPath(Path.Combine(ArenasDir, name));
            if (!Directory.Exists(source))
            {
                Log(LogLevel.Warn, $"Archive of [{name}]: folder {Path.Combine(ArenasDir, name)} does not exist; nothing moved.");
                return null;
            }

            var root = Path.GetFullPath(Path.Combine(ArenasDir, ArchiveDir));
            Directory.CreateDirectory(root);
            var folder = ArchiveFolders.NextName(Directory.EnumerateDirectories(root).Select(Path.GetFileName)!, name);
            Directory.Move(source, Path.Combine(root, folder)); // fails rather than overwrite
            return folder;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not move [{name}] to arenas/{ArchiveDir}/: {ex.Message}");
            return null;
        }
    }

    /// <summary>Moves the latest <c>arenas/_archive/&lt;name&gt;[-n]/</c> back to <c>arenas/&lt;name&gt;/</c>.</summary>
    private string? RestoreFromArchive(string name)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(ArenasDir, ArchiveDir));
            if (!Directory.Exists(root)) return null;
            var latest = ArchiveFolders.Latest(Directory.EnumerateDirectories(root).Select(Path.GetFileName)!, name);
            if (latest is null) return null;
            Directory.Move(Path.Combine(root, latest), Path.GetFullPath(Path.Combine(ArenasDir, name)));
            return latest;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not restore [{name}] from arenas/{ArchiveDir}/: {ex.Message}");
            return null;
        }
    }

    // ---- players in an arena ------------------------------------------------------------------

    private List<Player> PlayersIn(string name)
    {
        var list = new List<Player>();
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
                if (p.Arena is not null && string.Equals(p.Arena.BaseName, name, StringComparison.OrdinalIgnoreCase))
                    list.Add(p);
        }
        finally
        {
            _playerData.Unlock();
        }
        return list;
    }

    /// <summary>Sends everyone except arena staff (cmd_arenaadmin) to the public arena; returns the count.</summary>
    private int SendPlayersOut(string name, string message)
    {
        var count = 0;
        foreach (var p in PlayersIn(name))
        {
            RefreshGrant(p);
            if (_capabilityManager.HasCapability(p, AdminCapability)) continue;
            Reply(p, message);
            _arenaManager.SendToArena(p, "", 0, 0);
            count++;
        }
        return count;
    }

    /// <summary>Re-applies grants/revokes for everyone in the arena (after transfer and similar).</summary>
    private void RefreshGrantsIn(string name)
    {
        foreach (var p in PlayersIn(name))
            RefreshGrant(p);
    }

    private void RefreshGrant(Player p)
    {
        if (p.Arena is not null) ApplyGrant(p, p.Arena);
    }

    // ---- IArenaAuthorizationAdvisor: suspended/archived arenas are closed ------------------------

    bool IArenaAuthorizationAdvisor.IsAuthorizedToEnter(Player player, Arena arena, StringBuilder? errorMessage)
    {
        if (_store is null) return true;
        OwnedArena? owned;
        try
        {
            owned = _store.Get(arena.BaseName.ToLowerInvariant());
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Arena authorization check for [{arena.BaseName}] failed: {ex.Message}");
            return true; // never lock players out of the zone because of our store
        }

        if (owned is null || owned.Status == ArenaStatus.Active) return true;
        if (_capabilityManager.HasCapability(player, AdminCapability)) return true;

        errorMessage?.Append(owned.Status == ArenaStatus.Suspended
            ? "It is suspended by staff (see ?arenalog)."
            : "It is archived; its owner can ask staff to revive it.");
        return false;
    }
}
