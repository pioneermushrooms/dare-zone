using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Configuration;

namespace Dare.Arenas;

// Shared code paths for changing an owned arena, used by the in-game commands and the web pages.
// Every edit goes to a handle opened by OpenArenaConfigAsync on arenas/<name>/arena.conf (never
// the running arena's Cfg, which may be a shared fallback file such as conf/arena.conf).
// Callers have already checked the actor's role; these methods enforce the rules (allow-list,
// value checks, rate limit) and do the write. Call on the mainloop thread.
public sealed partial class ArenaOwnership
{
    internal const int SettingChangesPerMinute = 20;

    private static readonly string[] ShipSections =
        Enum.GetNames<ShipType>().Where(n => n != nameof(ShipType.Spec)).ToArray();

    private readonly RateLimiter _settingLimiter = new(SettingChangesPerMinute, () => DateTimeOffset.UtcNow);

    /// <summary>
    /// Every arena-scope setting the server documents, lowercased (section, key) → canonical names.
    /// Rebuilt on each use: config help fills in as modules load, and the build is cheap.
    /// </summary>
    internal Dictionary<(string, string), (string Section, string Key)> DocumentedArenaSettings()
    {
        var map = new Dictionary<(string, string), (string, string)>();
        _configHelp.Lock();
        try
        {
            foreach (var section in _configHelp.Sections)
            {
                if (!_configHelp.TryGetSectionKeys(section, out var keys))
                    continue;
                foreach (var key in keys)
                {
                    if (!_configHelp.TryGetSettingHelp(section, key, out var help)
                        || !help.Any(r => r.Attribute.Scope == ConfigScope.Arena))
                        continue;

                    // Ship settings are documented under the pseudo-section "All" and live in one
                    // section per ship (Warbird ... Shark), as the server's own ?quickfix expands them.
                    if (string.Equals(section, "All", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var ship in ShipSections)
                            map.TryAdd((ship.ToLowerInvariant(), key.ToLowerInvariant()), (ship, key));
                    }
                    else
                    {
                        map.TryAdd((section.ToLowerInvariant(), key.ToLowerInvariant()), (section, key));
                    }
                }
            }
        }
        finally
        {
            _configHelp.Unlock();
        }
        return map;
    }

    /// <summary>
    /// Opens <c>arenas/&lt;name&gt;/arena.conf</c> through the config manager (the same document as the
    /// running arena when that arena loaded its own file). Null when that file does not exist
    /// (<see cref="ArenaConfigFile.OwnPath"/>): the config search path would otherwise fall back to a
    /// shared file. <paramref name="name"/> must come from the store. Close with
    /// <see cref="CloseArenaConfig"/>. Folders of owned arenas only move at startup (archive), so
    /// the file can't disappear between the check and the open.
    /// </summary>
    internal async Task<ConfigHandle?> OpenArenaConfigAsync(string name)
    {
        if (ArenaConfigFile.OwnPath(ArenasDir, name) is null) return null;
        return await _configManager.OpenConfigFileAsync(name, null).ConfigureAwait(false);
    }

    internal void CloseArenaConfig(ConfigHandle handle) => _configManager.CloseConfigFile(handle);

    internal const string ConfigMissingMessage = "Your arena's settings file (arena.conf in its folder) is missing, so nothing was changed; ask staff.";

    /// <summary>
    /// The in-game counterpart of the web handlers: opens the arena's own arena.conf, re-checks
    /// that <paramref name="player"/> is still there with the role, runs <paramref name="action"/> on
    /// the mainloop and replies with its message. Refuses (changing nothing) when the file is missing.
    /// </summary>
    private async void WithOwnConfig(Player player, string arenaName, bool ownerOnly, Func<ConfigHandle, Result> action)
    {
        var playerName = player.Name!;
        ConfigHandle? cfg = null;
        try
        {
            // ConfigureAwait(true): continue on the mainloop (the server installs a SynchronizationContext).
            cfg = await OpenArenaConfigAsync(arenaName).ConfigureAwait(true);
            if (!ReferenceEquals(_playerData.FindPlayer(playerName), player))
                return;
            if (cfg is null)
            {
                Log(LogLevel.Warn, $"{playerName}: arenas/{arenaName}/{ArenaConfigFile.FileName} is missing; nothing changed.");
                Reply(player, ConfigMissingMessage);
                return;
            }
            if (!HasRole(arenaName, playerName, ownerOnly))
            {
                Reply(player, ownerOnly ? "Only this arena's owner can do that." : "Only this arena's owner or co-mods can do that.");
                return;
            }
            Reply(player, action(cfg).Message);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Arena edit by {playerName} in [{arenaName}] failed: {ex}");
            if (ReferenceEquals(_playerData.FindPlayer(playerName), player))
                Reply(player, "That failed; staff can see the details in the log.");
        }
        finally
        {
            if (cfg is not null) CloseArenaConfig(cfg);
        }
    }

    /// <summary>
    /// After an edit of the arena's own file: "" when the running arena (if any) already sees the
    /// written value, else a note that it runs on shared settings until <c>?recyclearena</c>.
    /// </summary>
    private string LiveNote(string arenaName, string section, string key, string value)
    {
        var running = _arenaManager.FindArena(arenaName);
        if (running?.Cfg is null || ArenaConfigFile.IsLive(_configManager.GetStr(running.Cfg, section, key), value))
            return "";
        MarkPendingRestart(arenaName, "settings");
        return " The running arena isn't using its own settings file yet, so this takes effect after ?recyclearena.";
    }

    /// <summary>
    /// Writes one owner-editable setting to <paramref name="cfg"/> (the handle from <see cref="OpenArenaConfigAsync"/>).
    /// <paramref name="arenaName"/> must come from the store. Returns "Section:Key: old -> new".
    /// </summary>
    internal Result ApplySetting(ConfigHandle cfg, string arenaName, string actor, string sectionKey, string rawValue)
    {
        if (LiveEditRefusal(arenaName) is { } locked)
            return new Result(false, locked);
        if (!SettingRef.TryParse(sectionKey, out var section, out var key))
            return new Result(false, "Use Section:Key, e.g. Warbird:BulletSpeed.");

        if (IsUploadOnly(section, key))
            return new Result(false, $"{section}:{key} changes only through map/graphics uploads (?arenamap / ?putlvz or the web page).");

        var documented = DocumentedArenaSettings();
        var policy = new SettingsPolicy(documented.Values);
        if (!policy.IsEditable(section, key))
            return new Result(false, $"{section}:{key} is not an arena setting owners can change.");
        (section, key) = documented[(section.ToLowerInvariant(), key.ToLowerInvariant())];

        if (!SettingValue.TryValidate(section, key, rawValue, out var value, out var error))
            return new Result(false, error);

        if (!_settingLimiter.TryTake(arenaName))
            return new Result(false, $"Too many setting changes for '{arenaName}': at most {SettingChangesPerMinute} per minute. Try again shortly.");

        var old = _configManager.GetStr(cfg, section, key);
        WriteArenaSetting(cfg, arenaName, section, key, value, $"{actor} with Dare.Arenas");
        Log(LogLevel.Info, $"{actor} set {section}:{key} in [{arenaName}]: '{old}' -> '{value}'.");
        return new Result(true, $"{section}:{key}: {old ?? "(unset)"} -> {(value.Length == 0 ? "(off)" : value)}{LiveNote(arenaName, section, key, value)}");
    }

    /// <summary>Reads one setting an owner may see (the editable ones plus General:Map/LevelFiles).</summary>
    internal Result ReadSetting(ConfigHandle cfg, string sectionKey)
    {
        if (!SettingRef.TryParse(sectionKey, out var section, out var key))
            return new Result(false, "Use Section:Key, e.g. Warbird:BulletSpeed.");

        if (!IsUploadOnly(section, key))
        {
            var documented = DocumentedArenaSettings();
            if (!new SettingsPolicy(documented.Values).IsEditable(section, key))
                return new Result(false, $"{section}:{key} is not an arena setting owners can see.");
            (section, key) = documented[(section.ToLowerInvariant(), key.ToLowerInvariant())];
        }

        var value = _configManager.GetStr(cfg, section, key);
        return new Result(true, $"{section}:{key} = {value ?? "(unset)"}");
    }

    private static bool IsUploadOnly(string section, string key) =>
        section.Equals("General", StringComparison.OrdinalIgnoreCase)
        && (key.Equals("Map", StringComparison.OrdinalIgnoreCase) || key.Equals("LevelFiles", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Permanent write that always lands in the arena's own arenas/&lt;arenaName&gt;/arena.conf. With
    /// the default options the server edits the line where the setting is currently defined, which
    /// for most settings is an #include'd shared file (conf/svs/...) and would change every arena
    /// that includes it. So the default (in-place) update is used only when <see cref="ConfOverride"/>
    /// shows the effective definition is our own override in arena.conf; otherwise
    /// <see cref="ModifyOptions.AppendOverrideOnly"/> appends a new override line there.
    /// </summary>
    private void WriteArenaSetting(ConfigHandle cfg, string arenaName, string section, string key, string value, string who)
    {
        string? text = null;
        try
        {
            var path = ArenaConfigFile.OwnPath(ArenasDir, arenaName);
            if (path is not null) text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"Could not read arena.conf of [{arenaName}] ({ex.Message}); appending the change.");
        }

        var options = ConfOverride.CanUpdateInPlace(text, section, key) ? ModifyOptions.None : ModifyOptions.AppendOverrideOnly;
        _configManager.SetStr(cfg, section, key, value,
            $"{who} on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC", permanent: true, options);
    }
}
