using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

/// <summary>
/// Player-owned arenas for Foundation: requests, public staff decisions, arena-scoped ownership grants.
/// All rules live in <see cref="Dare.Arenas.Core"/>; this module wires them to the server.
/// </summary>
[ModuleInfo("Player-owned arenas: ?requestarena, staff ?approvearena/?declinearena, arena-scoped owner and co-mod groups.")]
public sealed partial class ArenaOwnership : IModule
{
    public const string ModuleName = "Dare.Arenas";
    internal const string ArenasDir = "arenas";
    internal const string TemplateDir = "_template";
    internal const string DbPath = "data/dare-arenas.db";

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

    private ArenaStore? _store;

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
        IFileTransfer fileTransfer)
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
    }

    private ArenaStore Store => _store ?? throw new InvalidOperationException("Dare.Arenas store is not open.");

    bool IModule.Load(IComponentBroker broker)
    {
        try
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _store = new ArenaStore(DbPath, () => DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not open store '{DbPath}': {ex.Message}");
            return false;
        }

        PlayerActionCallback.Register(broker, Callback_PlayerAction);
        RegisterCommands();

        Log(LogLevel.Info, $"Loaded; store '{Path.GetFullPath(DbPath)}' open.");
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        UnregisterCommands();
        PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
        _store?.Dispose();
        _store = null;
        return true;
    }

    // ---- arena entry: grants + activity -----------------------------------------

    private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action != PlayerAction.EnterArena || arena is null || player.Name is null || _store is null)
            return;

        try
        {
            var baseName = arena.BaseName;
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
        var group = GrantLogic.GroupToApply(role, current);
        if (group is null)
            return;

        _groupManager.SetPermGroup(player, group, false, ModuleName);
        Log(LogLevel.Info, $"Granted '{group}' to {player.Name} in [{arena.BaseName}] (role {role}, was '{current}').");
        _chat.SendMessage(player, $"You are {(role == "owner" ? "the owner" : "a co-mod")} of this arena. Type ?myarena for details.");
    }

    /// <summary>
    /// Sets the player back to <c>default</c> in this arena's staff.conf section. Deliberately not
    /// <see cref="IGroupManager.RemoveGroup"/>: that writes <c>[(global)]</c> when the group came from
    /// there, and this module never edits the global section.
    /// </summary>
    private void Revoke(Player player, Arena arena, string why)
    {
        _groupManager.SetPermGroup(player, "default", false, $"{ModuleName}: {why}");
        Log(LogLevel.Info, $"Revoked arena group of {player.Name} in [{arena.BaseName}]: {why}.");
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
