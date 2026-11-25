using System.Text;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Staff overrides (?arenaadmin, smod and up via cmd_arenaadmin), the daily idle-archive check, and
// keeping players out of suspended/archived arenas. Every decision appends a public event (store).
// Archiving closes an arena at once but moves its folder to arenas/_archive/ only at the next
// start (ProcessPendingArchiveMoves): the server's ConfigManager never forgets a loaded file, so a
// folder moved while the server runs makes it log "failed to reload" every few seconds.
public sealed partial class ArenaOwnership : IArenaAuthorizationAdvisor
{
    internal const string ArchiveDir = "_archive";
    internal const string AdminCapability = "cmd_arenaadmin";
    internal const string CreateCapability = "cmd_createarena";
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
            the arena to another player (a claimed name: a Foundation account). archive: closes it; its folder
            moves to arenas/_archive/ at the next server restart. To take an arena from its owner,
            transfer or suspend it here: removing the owner's group with ?rmgroup does not last (it
            is granted again on their next entry).
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

        Log(LogLevel.Info, $"{staff.Name} archived [{name}]; folder moves to arenas/{ArchiveDir}/ at the next start.");
        var moved = SendPlayersOut(name, $"Arena '{name}' was archived by staff.");
        Reply(staff, $"{result.Message} Its folder moves to arenas/{ArchiveDir}/ at the next server restart.{(moved > 0 ? $" {moved} player(s) moved out." : "")}");
    }

    private void AdminRevive(Player staff, string name, ArenaStatus wasStatus)
    {
        // The folder must be there (or come back from the archive) before the arena is Active again.
        string? restored = null;
        var result = Store.Revive(name, staff.Name!, () =>
        {
            if (ArenaConfigFile.OwnPath(ArenasDir, name) is not null) return null; // incl. archived, move still pending
            if (ArenaFolderExists(name))
                return $"arenas/{name}/ has no {ArenaConfigFile.FileName}; restore it by hand first.";
            restored = RestoreFromArchive(name);
            if (restored is null)
                return $"arenas/{name}/ is missing and no archived copy was found in arenas/{ArchiveDir}/; restore it by hand first.";
            if (ArenaConfigFile.OwnPath(ArenasDir, name) is null)
                return $"the restored folder has no {ArenaConfigFile.FileName}; fix arenas/{name}/ by hand first.";
            return null;
        });
        if (!result.Ok)
        {
            Reply(staff, result.Message);
            return;
        }

        Log(LogLevel.Info, $"{staff.Name} revived [{name}] (was {wasStatus}).");
        Reply(staff, restored is null
            ? result.Message
            : $"{result.Message} Folder arenas/{ArchiveDir}/{restored}/ moved back to arenas/{name}/.");

        var owner = Store.Get(name)?.Owner;
        var online = owner is null ? null : _playerData.FindPlayer(owner);
        if (online is not null)
            Reply(online, $"Your arena '{name}' was reopened by {staff.Name}. Type ?go {name}.");
    }

    private void AdminTransfer(Player staff, string name, string oldOwner, string newOwnerRaw)
    {
        var online = _playerData.FindPlayer(newOwnerRaw);
        // Only a claimed name (canonical spelling): otherwise whoever first types ?passwd on it owns the arena.
        var newOwner = ResolveClaimedName(newOwnerRaw, online);
        if (newOwner is null)
        {
            Reply(staff, $"{newOwnerRaw} has not claimed that name (accounts: subspacefoundation.com); transfer only to a claimed name or to a player who is online and logged in with their password.");
            return;
        }

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

    // ---- ?createarena -------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "<name> [owner]",
        Description = """
            Creates a player-owned arena at once, without a request: arenas/<name>/ is copied from
            the template and you (or the named player, who must have a Foundation account) become
            its owner. Same name rules as ?makearena. Public in ?arenalog.
            """)]
    private void Command_createarena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Name is null) return;

        // The command manager checked cmd_createarena (smod/sysop through groupdef.dir/arenaadmin);
        // staff.conf groups only apply to authenticated names, but say so plainly if not.
        if (!player.Flags.Authenticated)
        {
            Reply(player, "Your name is not authenticated; log in with your password.");
            return;
        }

        var (rawName, ownerRaw) = SplitFirst(parameters);
        if (rawName.Length == 0) // the owner is the rest of the line: names may contain spaces
        {
            Reply(player, "Usage: ?createarena <name> [owner]   e.g. ?createarena skyrace Racer");
            return;
        }

        var (result, name, owner) = CreateArena(player.Name, rawName, ownerRaw.Length == 0 ? null : ownerRaw);
        Reply(player, result.Message);
        if (!result.Ok) return;

        var self = string.Equals(owner, player.Name, StringComparison.OrdinalIgnoreCase);
        Reply(player, self ? $"Type ?go {name}, then ?myarena." : $"Tell {owner} to type ?go {name}, then ?myarena.");
    }

    /// <summary>
    /// The shared create path for ?createarena and the web staff view: validates the name, resolves
    /// the owner (null: <paramref name="staff"/> themselves) to a claimed name, creates the folder
    /// inside the store transaction, recycles a running arena of that name and tells the owner.
    /// The caller has checked that <paramref name="staff"/> holds cmd_createarena. Mainloop.
    /// </summary>
    internal (Result Result, string Name, string Owner) CreateArena(string staff, string rawName, string? ownerRaw)
    {
        rawName = rawName.Trim();
        if (!ArenaName.TryValidate(rawName, ExistingArenaFolders(), out var name, out var error))
            return (new Result(false, $"Can't create '{rawName}': {error}"), "", "");

        string owner;
        Player? online = null;
        if (string.IsNullOrWhiteSpace(ownerRaw))
        {
            owner = staff;
        }
        else
        {
            ownerRaw = ownerRaw.Trim();
            if (ownerRaw.Length > 24 || ownerRaw.Any(char.IsControl))
                return (new Result(false, "That is not a valid player name."), "", "");
            online = _playerData.FindPlayer(ownerRaw);
            // Only a claimed name (canonical spelling): otherwise whoever first claims it owns the arena.
            var claimed = ResolveClaimedName(ownerRaw, online);
            if (claimed is null)
                return (new Result(false, $"{ownerRaw} has no Foundation account (subspacefoundation.com); an arena can only go to a claimed name."), "", "");
            owner = claimed;
            if (online is not null && !string.Equals(online.Name, owner, StringComparison.OrdinalIgnoreCase))
                online = null;
        }

        var result = Store.Create(name, staff, owner, ArenaFolderExists, () => TryCreateArenaFolder(name));
        if (!result.Ok) return (result, name, owner);

        Log(LogLevel.Info, $"{staff} created arena '{name}' for {owner}.");
        var (recycled, notRecycled) = RecycleRunning(name);
        var message = $"{result.Message} Folder arenas/{name}/ created from the template.";
        if (recycled.Count > 0) message += $" Recycled {string.Join(", ", recycled)} so it loads its new settings.";
        if (notRecycled.Count > 0)
        {
            message += $" Could not recycle {string.Join(", ", notRecycled)}: type ?recyclearena there so it uses its own settings.";
            Log(LogLevel.Warn, $"Arena '{name}' created while {string.Join(", ", notRecycled)} was running and could not be recycled.");
        }

        online ??= _playerData.FindPlayer(owner);
        if (online is not null && !string.Equals(online.Name, staff, StringComparison.OrdinalIgnoreCase))
            Reply(online, $"Staff ({staff}) created arena '{name}' for you! Type ?go {name} to enter it, then ?myarena.");
        return (new Result(true, message), name, owner);
    }

    // ---- daily idle archive ---------------------------------------------------------------

    private bool Timer_ArchiveIdle()
    {
        if (_store is null) return false;
        try
        {
            // Never archive an arena with players in it right now (its last-entered time can be old
            // when someone has stayed inside since).
            var occupied = PlayerCounts();
            var archived = Store.ArchiveIdle(IdleBeforeArchive, name => occupied.GetValueOrDefault(name) > 0);
            foreach (var name in archived)
            {
                SendPlayersOut(name, $"Arena '{name}' was archived after {(int)IdleBeforeArchive.TotalDays} days without visitors.");
                Log(LogLevel.Info, $"Archived idle arena [{name}] (no entry for {(int)IdleBeforeArchive.TotalDays} days); its folder moves to arenas/{ArchiveDir}/ at the next start.");
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
    /// At module load (server start): moves the folders of arenas archived since the last start to
    /// arenas/_archive/. Skips (and keeps pending) any arena that is running, e.g. on a module
    /// reload; a failed move stays pending and is retried at the next start.
    /// </summary>
    private void ProcessPendingArchiveMoves()
    {
        IReadOnlyList<string> pending;
        try
        {
            pending = Store.PendingArchiveMoves();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not read pending archive moves: {ex.Message}");
            return;
        }

        foreach (var name in pending)
        {
            try
            {
                var owned = Store.Get(name);
                if (owned is null || owned.Status != ArenaStatus.Archived)
                {
                    Store.ArchiveMoveDone(name); // revived or gone: nothing to move
                    continue;
                }
                if (_arenaManager.FindArena(name) is not null)
                {
                    Log(LogLevel.Warn, $"Archive of [{name}]: the arena is running; its folder moves at the next start.");
                    continue;
                }
                if (!ArenaFolderExists(name))
                {
                    Log(LogLevel.Warn, $"Archive of [{name}]: folder {Path.Combine(ArenasDir, name)} does not exist; nothing to move.");
                    Store.ArchiveMoveDone(name);
                    continue;
                }

                var folder = MoveToArchive(name);
                if (folder is null) continue; // logged; retried next start
                Store.ArchiveMoveDone(name);
                Log(LogLevel.Info, $"Archived arena [{name}]: folder moved to arenas/{ArchiveDir}/{folder}/.");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Pending archive move of [{name}] failed: {ex.Message}");
            }
        }
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

    /// <summary>
    /// The name to give a role to (co-mod, new owner): the online player's name when they are
    /// logged in with their password, else the account's name when it is a Foundation account,
    /// else the spelling stored in passwd.conf when the name is claimed there; null when the name
    /// is not claimed.
    /// </summary>
    internal string? ResolveClaimedName(string typed, Player? online)
    {
        var onlineAuthenticated = online is not null && online.Name is not null && online.Flags.Authenticated
                                  && string.Equals(online.Name, typed.Trim(), StringComparison.OrdinalIgnoreCase)
            ? online.Name
            : null;
        if (onlineAuthenticated is null && typed.Trim().Length > 0)
        {
            try
            {
                if (_accounts?.Find(typed.Trim()) is { } account) return account.Name;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warn, $"Account lookup for '{typed.Trim()}' failed: {ex.Message}");
            }
        }
        var value = _passwdConf is null ? null : _configManager.GetStr(_passwdConf, "users", typed.Trim());
        string? text = null;
        if (onlineAuthenticated is null && ClaimedName.IsClaim(value))
        {
            foreach (var path in new[] { Path.Combine("conf", PasswdFile), PasswdFile })
            {
                try
                {
                    if (File.Exists(path)) { text = File.ReadAllText(path); break; }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Warn, $"Could not read {path} for name spelling: {ex.Message}");
                }
            }
        }
        return ClaimedName.Resolve(typed, onlineAuthenticated, value, text);
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
