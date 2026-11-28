using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Configuration;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// Named settings sets (/arena/api/settingsets...) and many-settings-at-once edits
// (/arena/api/settings/preview, /arena/api/settings/apply). A set is a pilot's own list of
// Section:Key -> value pairs, kept in the store (thread-safe). Preview and apply read or write an
// arena's own arena.conf on the mainloop, through the same checks as a single setting edit.
// A settings file dropped on the page is parsed here too (SettingsFile), never on the client only.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    // a full set (2000 settings of up to 200 chars) or a settings file (256 KB) plus JSON overhead
    private const int MaxSettingsJsonBytes = 640 * 1024;

    private async Task<Reply?> SettingSetsRouteAsync(HttpListenerRequest req, string path, string method)
    {
        switch (path)
        {
            case "/api/settingsets": return method == "GET" ? SettingSetList(req) : MethodNotAllowed();
            case "/api/settingsets/get": return method == "GET" ? SettingSetGet(req) : MethodNotAllowed();
            case "/api/settingsets/save": return method == "POST" ? await SettingSetSaveAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/settingsets/remove":
            case "/api/settingsets/rename":
                return method == "POST" ? await SettingSetActionAsync(req, path).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/settings/preview":
            case "/api/settings/apply":
                return method == "POST" ? await SettingsBatchAsync(req, apply: path == "/api/settings/apply").ConfigureAwait(false) : MethodNotAllowed();
            default: return null;
        }
    }

    private Reply SettingSetList(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        return JsonReply(200, new { ok = true, max = ArenaStore.MaxPilotSettingSets, sets = _m.WebStore.SettingSets(player) });
    }

    private Reply SettingSetGet(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        if (!long.TryParse(req.QueryString["id"], out var id) || _m.WebStore.SettingSetOf(player, id) is not { } set)
            return JsonReply(404, new { ok = false, message = "That settings set isn't in your collection." });
        return JsonReply(200, new { ok = true, id, name = set.Name, settings = set.Settings });
    }

    /// <summary>POST {"name", "settings": {"Section:Key": "value"}, "id"?}: a new set, or (with id) replaces that one.</summary>
    private async Task<Reply> SettingSetSaveAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req, MaxSettingsJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;
        if (SettingsMap(body, out var bad) is not { } settings) return JsonReply(400, new { ok = false, message = bad });
        long? id = long.TryParse(Str(body, "id"), out var n) ? n : null;
        var result = _m.WebStore.SaveSettingSet(player, id, Str(body, "name") ?? "settings", settings, out var savedId);
        return JsonReply(result.Ok ? 200 : 400, new { ok = result.Ok, message = result.Message, id = savedId });
    }

    /// <summary>POST {"id"} to remove, {"id", "name"} to rename.</summary>
    private async Task<Reply> SettingSetActionAsync(HttpListenerRequest req, string path)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (!long.TryParse(Str(body, "id"), out var id))
            return JsonReply(400, new { ok = false, message = "Send {\"id\"}." });
        if (path == "/api/settingsets/remove") return ResultReply(_m.WebStore.RemoveSettingSet(player, id));
        if (string.IsNullOrWhiteSpace(Str(body, "name"))) return JsonReply(400, new { ok = false, message = "Send {\"id\", \"name\"}." });
        return ResultReply(_m.WebStore.RenameSettingSet(player, id, Str(body, "name")!));
    }

    /// <summary>
    /// POST {"arena", and one of "settings": {"Section:Key": "value"}, "set": id, "text": settings file}.
    /// Preview: the rows (change / same / skipped) without writing. Apply: writes the changes (owner or
    /// co-mod of that arena) and returns the same rows. A file's unreadable lines come back as fileSkipped.
    /// </summary>
    private async Task<Reply> SettingsBatchAsync(HttpListenerRequest req, bool apply)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req, MaxSettingsJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;

        var pick = Pick(_m.WebRolesFor(player), Str(body, "arena"));
        if (pick is null || string.IsNullOrWhiteSpace(Str(body, "arena")))
            return JsonReply(403, new { ok = false, message = "Only this arena's owner or co-mods can change its settings." });

        List<KeyValuePair<string, string>> settings;
        IReadOnlyList<SettingsFileSkip> fileSkipped = [];
        if (Str(body, "text") is { } text)
        {
            if (text.Length > SettingsFile.MaxBytes)
                return JsonReply(413, new { ok = false, message = "A settings file must be 256 KB or smaller." });
            var parsed = SettingsFile.Parse(text);
            settings = parsed.Entries.Select(e => KeyValuePair.Create($"{e.Section}:{e.Key}", e.Value)).ToList();
            fileSkipped = parsed.Skipped;
            if (settings.Count == 0 && fileSkipped.Count == 0)
                return JsonReply(400, new { ok = false, message = "That file has no settings in it (expected [Section] and Key=Value lines)." });
        }
        else if (long.TryParse(Str(body, "set"), out var setId))
        {
            if (_m.WebStore.SettingSetOf(player, setId) is not { } set)
                return JsonReply(404, new { ok = false, message = "That settings set isn't in your collection." });
            settings = set.Settings.ToList();
        }
        else if (SettingsMap(body, out var bad) is { } map)
        {
            settings = map.ToList();
        }
        else
        {
            return JsonReply(400, new { ok = false, message = bad });
        }
        if (settings.Count > ArenaOwnership.MaxSettingsPerBatch)
            return JsonReply(400, new { ok = false, message = $"At most {ArenaOwnership.MaxSettingsPerBatch} settings at once." });

        var arena = pick.Value.Arena;
        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        if (cfg is null)
            return JsonReply(409, new { ok = false, message = ArenaOwnership.ConfigMissingMessage });
        try
        {
            if (!apply)
            {
                var rows = await OnMain(() => _m.PreviewSettings(cfg, settings)).ConfigureAwait(false);
                return JsonReply(200, new { ok = true, arena, rows, fileSkipped });
            }
            var (result, applied) = await OnMain(() => _m.WebApplySettings(cfg, arena, player, settings)).ConfigureAwait(false);
            return JsonReply(result.Ok ? 200 : 400, new { ok = result.Ok, message = result.Message, arena, rows = applied, fileSkipped });
        }
        finally
        {
            _m.CloseArenaConfig(cfg);
        }
    }

    /// <summary>The body's "settings" object as Section:Key -> value (values as text), or null with a message.</summary>
    private static Dictionary<string, string>? SettingsMap(JsonElement? body, out string error)
    {
        error = "Send {\"settings\": {\"Section:Key\": \"value\", ...}}.";
        if (body is not { } b || !b.TryGetProperty("settings", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in s.EnumerateObject())
        {
            map[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => "",
            };
            if (map.Count > ArenaStore.MaxSettingsPerSet) { error = $"At most {ArenaStore.MaxSettingsPerSet} settings at once."; return null; }
        }
        return map;
    }
}

public sealed partial class ArenaOwnership
{
    internal (Result Result, List<SettingChange> Rows) WebApplySettings(ConfigHandle cfg, string arena, string player, IReadOnlyCollection<KeyValuePair<string, string>> settings)
    {
        if (!HasRole(arena, player, ownerOnly: false))
            return (new Result(false, "You are no longer an owner or co-mod of this arena."), []);
        var (result, rows) = ApplySettings(cfg, arena, player, settings);
        if (result.Ok) Log(LogLevel.Info, $"(web) {player} applied {settings.Count} settings to [{arena}]: {result.Message}");
        return (result, rows);
    }
}
