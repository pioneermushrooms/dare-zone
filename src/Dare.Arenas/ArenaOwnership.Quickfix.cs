using System.Text;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

namespace Dare.Arenas;

// The in-game settings editor: Continuum's own (it opens when the zone sends server.set in answer
// to ?quickfix / ?getsettings) and the Dare browser client's (Esc menu, E). This replaces the core
// SS.Core.Modules.Quickfix module, which deploy/apply-local.sh comments out of Modules.config:
// that one is open only to "changesettings" (smod/sysop) and writes wherever a setting is defined,
// which for a player-owned arena is usually a shared conf/svs file read by every arena.
//
// Here:
//  - in a player-owned arena (or draft), its owner and co-mods, and staff with changesettings, see
//    the owner allow-list with the arena's own values, and every change goes through ApplySettings:
//    the web page's checks, written only to arenas/<name>/arena.conf, rate limited;
//  - in any other arena, changesettings staff get the core behaviour (every arena-scope setting,
//    written through the arena's config), with the same value checks;
//  - everyone else is told who can change settings there.
public sealed partial class ArenaOwnership
{
    internal const string SettingsEditorReplyPrefix = "Arena settings editor: ";
    private const int MaxSkippedLines = 8;

    private INetwork? _network;

    private void LoadQuickfix(IComponentBroker broker)
    {
        _network = broker.GetInterface<INetwork>();
        _network?.AddPacket(C2SPacketType.SettingChange, Packet_SettingChange);
        Add("quickfix", Command_quickfix);
        Add("getsettings", Command_quickfix);
        Add("settingseditor", Command_settingseditor);
    }

    private void UnloadQuickfix(IComponentBroker broker)
    {
        if (_network is null) return;
        _network.RemovePacket(C2SPacketType.SettingChange, Packet_SettingChange);
        broker.ReleaseInterface(ref _network);
    }

    private bool CanChangeAllSettings(Player player) =>
        _capabilityManager.HasCapability(player, Constants.Capabilities.ChangeSettings);

    /// <summary>
    /// The store's name of the player-owned arena (or draft) the player stands in, when this player
    /// may edit its settings there: owner, co-mod, or changesettings staff. Null otherwise.
    /// </summary>
    private string? EditableOwnedArena(Player player, out bool owned)
    {
        owned = false;
        if (player.Arena is not { } arena || player.Name is null || _store is null) return null;
        var record = _store.OwnedOrDraft(arena.BaseName.ToLowerInvariant());
        if (record is null) return null;
        owned = true;
        if (CanChangeAllSettings(player)) return record.Name;
        if (!player.Flags.Authenticated) return null;
        return _store.RoleIn(record.Name, player.Name) is not null ? record.Name : null;
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[text]",
        Description = """
            Opens the arena settings editor (Continuum's settings window; in the browser client,
            Esc then E). Owners and co-mods can use it in their own arena: it lists the settings
            ?arenaset can change and saves to the arena's own settings file. With text, lists only
            the settings whose section or key contains it, e.g. ?quickfix bullet.
            """)]
    private void Command_quickfix(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player.Arena is not { } arena)
        {
            Reply(player, "You must be in an arena to view or change settings.");
            return;
        }
        var filter = parameters.Trim().ToString();
        var arenaName = EditableOwnedArena(player, out var owned);
        if (arenaName is not null)
        {
            SendOwnedSettings(player, arenaName, filter);
            return;
        }
        if (!owned && CanChangeAllSettings(player) && arena.Cfg is { } cfg)
        {
            SendSettingsFile(player, ZoneSettingRows(cfg, filter), skipped: 0);
            return;
        }
        Reply(player, owned
            ? "Only this arena's owner and co-mods can change its settings (log in with your password if you are one)."
            : "Settings here can't be changed in game. Your own arena's can: see ?myarena.");
    }

    [CommandHelp(
        Targets = CommandTarget.None,
        Description = "Says whether you can open the arena settings editor (?quickfix) in this arena.")]
    private void Command_settingseditor(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        // The browser client asks on entering each arena and hides this exact reply; it shows
        // Esc then E only on "available".
        var yes = player.Arena is not null
                  && (EditableOwnedArena(player, out var owned) is not null || (!owned && CanChangeAllSettings(player)));
        Reply(player, SettingsEditorReplyPrefix + (yes ? "available (Esc then E, or ?quickfix)." : "not available here."));
    }

    private async void SendOwnedSettings(Player player, string arenaName, string filter)
    {
        var playerName = player.Name!;
        ConfigHandle? cfg = null;
        try
        {
            cfg = await OpenArenaConfigAsync(arenaName).ConfigureAwait(true);
            if (!ReferenceEquals(_playerData.FindPlayer(playerName), player)) return;
            if (cfg is null)
            {
                Reply(player, ConfigMissingMessage);
                return;
            }
            var (rows, skipped) = OwnedSettingRows(cfg, filter);
            SendSettingsFile(player, rows, skipped);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"?quickfix for {playerName} in [{arenaName}] failed: {ex}");
            if (ReferenceEquals(_playerData.FindPlayer(playerName), player))
                Reply(player, "That failed; staff can see the details in the log.");
        }
        finally
        {
            if (cfg is not null) CloseArenaConfig(cfg);
        }
    }

    /// <summary>The owner allow-list, with this arena's own values, as server.set rows.</summary>
    private (List<string> Lines, int Skipped) OwnedSettingRows(ConfigHandle cfg, string filter)
    {
        var documented = DocumentedArenaSettings();
        var policy = new SettingsPolicy(documented.Values);
        var lines = new List<string>();
        var skipped = 0;
        foreach (var (section, key) in documented.Values
                     .Where(v => policy.IsEditable(v.Section, v.Key) && QuickfixFile.Matches(filter, v.Section, v.Key))
                     .OrderBy(v => v.Section, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (TrySettingLine(cfg, section, key, out var line)) lines.Add(line);
            else skipped++;
        }
        return (lines, skipped);
    }

    /// <summary>Every arena-scope setting the server documents (staff, outside owned arenas).</summary>
    private List<string> ZoneSettingRows(ConfigHandle cfg, string filter)
    {
        var lines = new List<string>();
        foreach (var (section, key) in DocumentedArenaSettings().Values
                     .Where(v => QuickfixFile.Matches(filter, v.Section, v.Key))
                     .OrderBy(v => v.Section, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (TrySettingLine(cfg, section, key, out var line)) lines.Add(line);
        }
        return lines;
    }

    private bool TrySettingLine(ConfigHandle cfg, string section, string key, out string line)
    {
        var help = SettingHelp(section, key);
        var spec = help is null ? SettingSpec.AnyText : SettingSpec.From(help.Type, help.Min, help.Max);
        string text;
        if (ShipSections.Contains(section) && section != ShipSections[0])
        {
            // The same help for all eight ships made the file several times larger (and it
            // travels at file-transfer speed): ships after the first point at the Warbird's.
            text = QuickfixFile.SameAs(ShipSections[0], key);
        }
        else
        {
            text = QuickfixFile.Shorten(help?.Description ?? "");
            // the range only where it says more than the number type does
            var allowed = spec.Kind == SettingKind.Int && help?.Min is null && help?.Max is null ? "" : spec.Describe();
            if (allowed.Length > 0) text += $" [{allowed}]";
            if (!string.IsNullOrEmpty(help?.DefaultValue)) text += $" [default {help.DefaultValue}]";
        }
        var (min, max) = spec.Kind is SettingKind.Int or SettingKind.Bool && spec.Min is { } lo && spec.Max is { } hi
            ? (lo.ToString(), hi.ToString())
            : (help?.Min ?? "", help?.Max ?? "");
        return QuickfixFile.TryLine(
            new QuickfixFile.Row(section, key, _configManager.GetStr(cfg, section, key) ?? "<unset>", min, max, text.Trim()),
            out line);
    }

    private void SendSettingsFile(Player player, List<string> lines, int skipped)
    {
        if (lines.Count == 0)
        {
            Reply(player, "No settings matched.");
            return;
        }
        var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, StringUtils.DefaultEncoding, leaveOpen: true))
            foreach (var line in lines) writer.Write(line);
        stream.Position = 0;
        Reply(player, $"Sending {lines.Count} settings...");
        if (skipped > 0)
            Reply(player, $"{skipped} setting(s) with ':' in their value aren't listed; change them with ?arenaset or the web page.");
        if (!_fileTransfer.SendFile(player, stream, "server.set"))
            Log(LogLevel.Warn, $"Could not send server.set to {player.Name}.");
    }

    // ---- changes from the editor (C2S 0x1D) ----------------------------------------------------

    private void Packet_SettingChange(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
    {
        if (data.Length < 1) return;
        if (!QuickfixFile.TryParseChanges(data[1..], StringUtils.DefaultEncoding, out var changes, out var permanent))
        {
            _logManager.LogP(LogLevel.Malicious, ModuleName, player, "Badly formatted setting change.");
            return;
        }
        if (changes.Count == 0) return;
        _mainloop.QueueMainWorkItem(ApplyEditorChanges, (player, changes, permanent));
    }

    private void ApplyEditorChanges((Player Player, List<QuickfixFile.Change> Changes, bool Permanent) work)
    {
        var (player, changes, permanent) = work;
        if (player.Name is null || !ReferenceEquals(_playerData.FindPlayer(player.Name), player)) return;
        if (player.Arena is not { } arena) return;

        var arenaName = EditableOwnedArena(player, out var owned);
        if (arenaName is not null)
        {
            var actor = player.Name;
            var settings = changes.Select(c => new KeyValuePair<string, string>(c.SectionKey, c.Value)).ToList();
            WithOwnConfig(player, arenaName, ownerOnly: false, cfg => ReportBatch(player, ApplySettings(cfg, arenaName, actor, settings)),
                staffToo: CanChangeAllSettings(player));
            return;
        }
        if (!owned && CanChangeAllSettings(player) && arena.Cfg is { } cfg)
        {
            ApplyZoneChanges(player, cfg, changes, permanent);
            return;
        }
        Reply(player, "You are not allowed to change settings in this arena.");
    }

    /// <summary>The batch's message, then up to <see cref="MaxSkippedLines"/> skipped settings with why.</summary>
    private Result ReportBatch(Player player, (Result Result, List<SettingChange> Rows) batch)
    {
        var skipped = batch.Rows.Where(r => r.Status == "skipped").ToList();
        foreach (var row in skipped.Take(MaxSkippedLines))
            Reply(player, $"  {row.Section}{(row.Key.Length > 0 ? ":" + row.Key : "")} not changed: {row.Reason}");
        if (skipped.Count > MaxSkippedLines)
            Reply(player, $"  ...and {skipped.Count - MaxSkippedLines} more not changed.");
        return batch.Result;
    }

    /// <summary>Staff outside player-owned arenas: the core editor's behaviour, with value checks.</summary>
    private void ApplyZoneChanges(Player player, ConfigHandle cfg, List<QuickfixFile.Change> changes, bool permanent)
    {
        var documented = DocumentedArenaSettings();
        var comment = $"Set by {player.Name} with ?quickfix on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
        var changed = 0;
        foreach (var c in changes)
        {
            if (!SettingRef.TryParse(c.SectionKey, out var section, out var key)
                || !documented.TryGetValue((section.ToLowerInvariant(), key.ToLowerInvariant()), out var canonical))
            {
                Reply(player, $"  {c.SectionKey} not changed: not an arena setting the server documents.");
                continue;
            }
            (section, key) = canonical;
            if (!SettingValue.TryValidate(section, key, c.Value, out var value, out var error)
                || !SpecOf(section, key).TryCheck($"{section}:{key}", value, out value, out error))
            {
                Reply(player, $"  {section}:{key} not changed: {error}");
                continue;
            }
            _logManager.LogP(LogLevel.Info, ModuleName, player, $"Setting {section}:{key} = {value} (?quickfix)");
            _configManager.SetStr(cfg, section, key, value, comment, permanent);
            changed++;
        }
        if (changed > 0)
            Reply(player, $"Changed {changed} setting(s) of this arena{(permanent ? "" : " until it is recycled")}.");
    }
}
