using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Configuration;

namespace Dare.Arenas;

// Shared code paths for changing an owned arena: used by ?arenaset/?arenaget here and by the
// web pages (Task 7). Callers have already checked the actor's role; these methods enforce the
// rules (allow-list, value checks, rate limit) and do the write. Call on the mainloop thread.
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
    /// Writes one owner-editable setting to <paramref name="cfg"/> (the arena's arena.conf handle).
    /// <paramref name="arenaName"/> must come from the store. Returns "Section:Key: old -> new".
    /// </summary>
    internal Result ApplySetting(ConfigHandle cfg, string arenaName, string actor, string sectionKey, string rawValue)
    {
        if (!SettingRef.TryParse(sectionKey, out var section, out var key))
            return new Result(false, "Use Section:Key, e.g. Warbird:BulletSpeed.");

        if (IsUploadOnly(section, key))
            return new Result(false, $"{section}:{key} changes only through map/graphics uploads (?arenamap / ?putlvz or the web page).");

        var documented = DocumentedArenaSettings();
        var policy = new SettingsPolicy(documented.Values);
        if (!policy.IsEditable(section, key))
            return new Result(false, $"{section}:{key} is not an arena setting owners can change.");
        (section, key) = documented[(section.ToLowerInvariant(), key.ToLowerInvariant())];

        if (!SettingValue.TryValidate(rawValue, out var value, out var error))
            return new Result(false, error);

        if (!_settingLimiter.TryTake(arenaName))
            return new Result(false, $"Too many setting changes for '{arenaName}': at most {SettingChangesPerMinute} per minute. Try again shortly.");

        var old = _configManager.GetStr(cfg, section, key);
        WriteArenaSetting(cfg, arenaName, section, key, value, $"{actor} with Dare.Arenas");
        Log(LogLevel.Info, $"{actor} set {section}:{key} in [{arenaName}]: '{old}' -> '{value}'.");
        return new Result(true, $"{section}:{key}: {old ?? "(unset)"} -> {value}");
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
            var path = ArenaPaths.Resolve(ArenasDir, arenaName, "arena.conf");
            if (path is not null && File.Exists(path)) text = File.ReadAllText(path);
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
