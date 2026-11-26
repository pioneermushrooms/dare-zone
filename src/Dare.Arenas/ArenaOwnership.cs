using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Configuration;

namespace Dare.Arenas;

/// <summary>
/// Player-owned arenas for Foundation: requests, public staff decisions, arena-scoped ownership grants.
/// All rules live in <see cref="Dare.Arenas.Core"/>; this module wires them to the server.
/// </summary>
[ModuleInfo("Player-owned arenas: ?makearena (no approval), staff ?createarena/?arenaadmin, arena-scoped owner and co-mod groups.")]
public sealed partial class ArenaOwnership : IModule
{
    public const string ModuleName = "Dare.Arenas";
    internal const string ArenasDir = "arenas";
    internal const string TemplateDir = "_template";
    internal const string MapsDir = "maps"; // the server's shared maps (General:Map falls back here)
    internal const string DbPath = "data/dare-arenas.db";
    internal const string PasswdFile = "passwd.conf";

    private readonly IComponentBroker _broker;
    private readonly ICommandManager _commandManager;
    private readonly IChat _chat;
    private readonly IGroupManager _groupManager;
    private readonly IArenaManager _arenaManager;
    private readonly IPlayerData _playerData;
    private readonly IConfigManager _configManager;
    private readonly IConfigHelp _configHelp;
    private readonly ILogManager _logManager;
    private readonly IMainloopTimer _mainloopTimer;
    private readonly IFileTransfer _fileTransfer;
    private readonly ICapabilityManager _capabilityManager;
    private readonly IMainloop _mainloop;
    private readonly IGame _game;

    private ArenaStore? _store;
    // Foundation accounts (same db file, own connection). Game logins are answered by the
    // FoundationAuth module; here the store only resolves claimed names for roles.
    private AccountStore? _accounts;

    // staff.conf (for players' [(global)] groups) and passwd.conf (claimed names). Same documents
    // the CapabilityManager and AuthFile modules use, so values are current in memory.
    private ConfigHandle? _staffConf;
    private ConfigHandle? _passwdConf;

    public ArenaOwnership(
        IComponentBroker broker,
        ICommandManager commandManager,
        IChat chat,
        IGroupManager groupManager,
        IArenaManager arenaManager,
        IPlayerData playerData,
        IConfigManager configManager,
        IConfigHelp configHelp,
        ILogManager logManager,
        IMainloopTimer mainloopTimer,
        IFileTransfer fileTransfer,
        ICapabilityManager capabilityManager,
        IMainloop mainloop,
        IGame game)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _configHelp = configHelp ?? throw new ArgumentNullException(nameof(configHelp));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
        _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        _game = game ?? throw new ArgumentNullException(nameof(game));
    }

    private ArenaStore Store => _store ?? throw new InvalidOperationException("Dare.Arenas store is not open.");

    bool IModule.Load(IComponentBroker broker)
    {
        try
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _store = new ArenaStore(DbPath, () => DateTimeOffset.UtcNow);
            _accounts = new AccountStore(DbPath, () => DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not open store '{DbPath}': {ex.Message}");
            return false;
        }

        try
        {
            // Load runs before the mainloop; Task.Run keeps the config manager's awaits off this thread.
            _staffConf = Task.Run(() => _configManager.OpenConfigFileAsync(null, "staff.conf")).GetAwaiter().GetResult();
            _passwdConf = Task.Run(() => _configManager.OpenConfigFileAsync(null, PasswdFile)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not open staff.conf/passwd.conf: {ex.Message}");
        }
        if (_staffConf is null || _passwdConf is null)
        {
            Log(LogLevel.Error, "staff.conf and passwd.conf are required (global groups, claimed names); not loading.");
            CloseConfs();
            _store.Dispose();
            _store = null;
            _accounts?.Dispose();
            _accounts = null;
            return false;
        }

        // Archived arenas' folders move now, before any arena with that name can be created.
        ProcessPendingArchiveMoves();
        PromoteDrafts();

        PlayerActionCallback.Register(broker, Callback_PlayerAction);
        LoadVoice();
        RegisterCommands();
        RegisterStaff(broker);
        LoadZoneList(broker);
        LoadWebChat(broker);
        StartWeb();
        StartBots();

        Log(LogLevel.Info, $"Loaded; store '{Path.GetFullPath(DbPath)}' open.");
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        StopBots();
        StopWeb(); // first: no new web work reaches the mainloop after this
        UnloadWebChat(broker);
        UnloadZoneList(broker);
        UnregisterStaff(broker);
        UnregisterCommands();
        PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
        CloseConfs();
        _store?.Dispose();
        _store = null;
        _accounts?.Dispose();
        _accounts = null;
        return true;
    }

    private void CloseConfs()
    {
        if (_staffConf is not null) _configManager.CloseConfigFile(_staffConf);
        if (_passwdConf is not null) _configManager.CloseConfigFile(_passwdConf);
        _staffConf = null;
        _passwdConf = null;
    }

    // ---- arena entry: grants + activity -----------------------------------------

    private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action != PlayerAction.EnterArena || arena is null || player.Name is null || IsBot(player.Name) || _store is null)
            return;

        try
        {
            var baseName = arena.BaseName;
            if (DraftRules.IsDraft(baseName))
            {
                // a plain private arena (?go #anything) is nobody's: leave it alone
                if (_store.Draft(baseName) is null || !DraftEntryAllowed(player, arena))
                    return;
                ApplyGrant(player, arena);
                _store.TouchDraft(baseName);
                return;
            }
            if (_store.Get(baseName) is null)
                return;

            ApplyGrant(player, arena);
            _store.MarkEntered(baseName);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Enter-arena handling failed for {player.Name} in {arena.Name}: {ex.Message}");
        }
    }

    /// <summary>Sets the arena-scoped group that the player's role in <paramref name="arena"/> calls for.</summary>
    private void ApplyGrant(Player player, Arena arena)
    {
        if (player.Name is null || !ReferenceEquals(player.Arena, arena))
            return;

        // A module group with no role behind it (co-mod removed, transferred, suspended) is stale.
        var currentGroup = _groupManager.GetGroup(player);
        var global = GlobalGroupOf(player.Name);
        if (GrantLogic.ShouldRevoke(Store.RoleIn(arena.BaseName, player.Name), currentGroup))
        {
            Revoke(player, arena, $"no longer has a role (was '{currentGroup}')");
            return;
        }

        // Same rule as the server's CapabilityManager: name-based groups only for players who
        // proved the name (password/biller). Otherwise anyone could log in as the owner.
        if (!player.Flags.Authenticated)
        {
            if (Store.RoleIn(arena.BaseName, player.Name) is not null)
                _chat.SendMessage(player, "Your name is not authenticated, so your arena powers are not active. Log in with your password.");
            return;
        }

        var role = Store.RoleIn(arena.BaseName, player.Name);
        var current = _groupManager.GetGroup(player);
        var group = GrantLogic.GroupToApply(role, current, global);
        if (group is null)
        {
            // Global staff keep their own group; their staff group carries the owner commands.
            if (role is not null && GrantLogic.IsGlobalStaff(global))
                _chat.SendMessage(player, $"You are {(role == "owner" ? "the owner" : "a co-mod")} of this arena; your staff group '{global}' already has the arena commands. Type ?myarena for details.");
            return;
        }

        _groupManager.SetPermGroup(player, group, false, ModuleName);
        if (GrantLogic.IsGlobalStaff(global))
        {
            // an arena group granted before global staff were excluded: put back their own group
            Log(LogLevel.Info, $"Restored '{group}' (global group) for {player.Name} in [{arena.BaseName}] (was '{current}').");
            return;
        }
        Log(LogLevel.Info, $"Granted '{group}' to {player.Name} in [{arena.BaseName}] (role {role}, was '{current}').");
        _chat.SendMessage(player, $"You are {(role == "owner" ? "the owner" : "a co-mod")} of this arena. Type ?myarena for details.");
    }

    /// <summary>The player's group in staff.conf <c>[(global)]</c>, or null.</summary>
    internal string? GlobalGroupOf(string playerName) =>
        _staffConf is null ? null : _configManager.GetStr(_staffConf, Constants.ArenaGroup_Global, playerName);

    /// <summary>
    /// Sets the player back to their own <c>[(global)]</c> group (<c>default</c> when none) in this
    /// arena's staff.conf section. The v4.0.0 config API can't delete the arena-scoped key, and
    /// writing <c>default</c> would demote global staff in this arena. Deliberately not
    /// <see cref="IGroupManager.RemoveGroup"/>: that writes <c>[(global)]</c> when the group came from
    /// there, and this module never edits the global section.
    /// </summary>
    private void Revoke(Player player, Arena arena, string why)
    {
        var to = GrantLogic.RevokeTo(player.Name is null ? null : GlobalGroupOf(player.Name));
        _groupManager.SetPermGroup(player, to, false, $"{ModuleName}: {why}");
        Log(LogLevel.Info, $"Revoked arena group of {player.Name} in [{arena.BaseName}] (now '{to}'): {why}.");
    }

    // ---- helpers ------------------------------------------------------------------

    internal void Log(LogLevel level, string message) => _logManager.LogM(level, ModuleName, message);

    /// <summary>Folder names under <c>arenas/</c> (used to reserve names).</summary>
    private static HashSet<string> ExistingArenaFolders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ArenasDir)) return set;
        foreach (var d in Directory.EnumerateDirectories(ArenasDir))
            set.Add(Path.GetFileName(d));
        return set;
    }

    private static bool ArenaFolderExists(string name) => Directory.Exists(Path.Combine(ArenasDir, name));

    /// <summary>
    /// <see cref="CreateArenaFolder"/> for the approval transaction: null on success, else a short
    /// reason (logged), with any half-copied folder removed again.
    /// </summary>
    private string? TryCreateArenaFolder(string name)
    {
        var target = Path.GetFullPath(Path.Combine(ArenasDir, name));
        var existed = Directory.Exists(target);
        try
        {
            CreateArenaFolder(name);
            if (ArenaConfigFile.OwnPath(ArenasDir, name) is null)
                throw new IOException($"the template has no {ArenaConfigFile.FileName}");
            Log(LogLevel.Info, $"Created folder {Path.Combine(ArenasDir, name)} from template.");
            return null;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Creating folder for arena '{name}' failed: {ex.Message}");
            if (!existed && Directory.Exists(target))
            {
                try { Directory.Delete(target, recursive: true); }
                catch (Exception cleanup) { Log(LogLevel.Error, $"Could not remove half-created {target}: {cleanup.Message}"); }
            }
            return ex.Message;
        }
    }

    /// <summary>Copies <c>arenas/_template/</c> into a new <c>arenas/&lt;name&gt;/</c>; never overwrites.</summary>
    private static void CreateArenaFolder(string name)
    {
        var source = Path.GetFullPath(Path.Combine(ArenasDir, TemplateDir));
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"template folder '{Path.Combine(ArenasDir, TemplateDir)}' is missing");

        var target = Path.GetFullPath(Path.Combine(ArenasDir, name));
        if (Directory.Exists(target))
            throw new IOException($"folder '{Path.Combine(ArenasDir, name)}' already exists");

        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: false);
    }
}
