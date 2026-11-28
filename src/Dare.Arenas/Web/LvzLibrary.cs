using System.Net;
using Dare.Arenas.Core;
using SS.Core;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// The LVZ editor's server side. A signed-in pilot's saved .lvz packages (/arena/api/lvzlib...),
// drafts until published, and read access to an arena's own .lvz files (/arena/api/lvz/arena) for
// its owner and co-mods. Publishing goes through the existing upload (/api/upload?kind=lvz), which
// keeps every arena rule (owner only, live-edit lock, 10 files, safe names) in one place.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private const int MaxLvzBytes = 5 * 1024 * 1024;

    private async Task<Reply?> LvzRouteAsync(HttpListenerRequest req, string path, string method)
    {
        switch (path)
        {
            case "/api/lvzlib": return method == "GET" ? LvzLibList(req) : MethodNotAllowed();
            case "/api/lvzlib/file": return method == "GET" ? LvzLibFile(req) : MethodNotAllowed();
            case "/api/lvzlib/save": return method == "POST" ? await LvzLibSaveAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/lvzlib/remove":
            case "/api/lvzlib/rename":
            case "/api/lvzlib/duplicate":
                return method == "POST" ? await LvzLibActionAsync(req, path).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/lvz/arena": return method == "GET" ? await ArenaLvzAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            default: return null;
        }
    }

    private Reply LvzLibList(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        return JsonReply(200, new { ok = true, max = ArenaStore.MaxPilotLvz, files = _m.WebStore.LvzList(player) });
    }

    private Reply LvzLibFile(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        if (!long.TryParse(req.QueryString["id"], out var id) || _m.WebStore.LvzFileOf(player, id) is not { } f)
            return JsonReply(404, new { ok = false, message = "That .lvz isn't in your collection." });
        return new Reply(200, "application/octet-stream", f.Data, [("X-Lvz-Name", Uri.EscapeDataString(f.Name))]);
    }

    /// <summary>POST ?name=&amp;id= with the .lvz bytes: a new one, or (with id) replaces that one.</summary>
    private async Task<Reply> LvzLibSaveAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        if (req.ContentLength64 > MaxLvzBytes) return JsonReply(413, new { ok = false, message = "Graphics files must be 5 MB or smaller." });
        var content = await ReadBodyAsync(req, MaxLvzBytes, UploadBodyTimeout).ConfigureAwait(false);
        if (content is null) return JsonReply(413, new { ok = false, message = "Graphics files must be 5 MB or smaller." });
        var name = ArenaStore.LvzFileName(req.QueryString["name"]);
        if (!UploadPolicy.TryCheck(UploadKind.Lvz, name, content, 0, out var error)) return JsonReply(400, new { ok = false, message = error });
        try { LvzFile.ReadFiles(content, namesOnly: true); }
        catch (InvalidDataException) { return JsonReply(400, new { ok = false, message = "That .lvz file is damaged." }); }
        long? id = long.TryParse(req.QueryString["id"], out var n) ? n : null;
        var result = _m.WebStore.SaveLvz(player, id, name, content, out var savedId);
        return JsonReply(result.Ok ? 200 : 400, new { ok = result.Ok, message = result.Message, id = savedId, name });
    }

    private async Task<Reply> LvzLibActionAsync(HttpListenerRequest req, string path)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (!long.TryParse(Str(body, "id"), out var id)) return JsonReply(400, new { ok = false, message = "Send {\"id\"}." });
        switch (path)
        {
            case "/api/lvzlib/remove": return ResultReply(_m.WebStore.RemoveLvz(player, id));
            case "/api/lvzlib/rename":
                if (string.IsNullOrWhiteSpace(Str(body, "name"))) return JsonReply(400, new { ok = false, message = "Send {\"id\", \"name\"}." });
                return ResultReply(_m.WebStore.RenameLvz(player, id, Str(body, "name")!));
            default:
                var r = _m.WebStore.DuplicateLvz(player, id, out var copy);
                return JsonReply(r.Ok ? 200 : 400, new { ok = r.Ok, message = r.Message, id = copy });
        }
    }

    /// <summary>GET ?arena=&amp;name=: one of the arena's .lvz files (listed in its LevelFiles), for its owner or co-mods.</summary>
    private async Task<Reply> ArenaLvzAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        var pick = Pick(_m.WebRolesFor(player), req.QueryString["arena"]);
        if (pick is null) return JsonReply(403, new { ok = false, message = "You are not an owner or co-mod of that arena." });
        var arena = pick.Value.Arena;
        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        if (cfg is null) return JsonReply(409, new { ok = false, message = ArenaOwnership.ConfigMissingMessage });
        string? listed;
        try { listed = await OnMain(() => _m.LevelFilesSetting(cfg)).ConfigureAwait(false); }
        finally { _m.CloseArenaConfig(cfg); }
        var entry = LevelFiles.Find(listed, req.QueryString["name"] ?? "");
        if (entry is null || !LevelFiles.IsSafeFileName(entry)) return JsonReply(404, new { ok = false, message = "That file isn't one of the arena's .lvz files." });
        foreach (var path in new[] { ArenaPaths.Resolve(ArenaOwnership.ArenasDir, arena, entry), Path.Combine(ArenaOwnership.MapsDir, entry) })
        {
            if (path is null) continue;
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) continue;
            if (info.Length == 0 || info.Length > MaxLvzBytes) return JsonReply(409, new { ok = false, message = "That .lvz can't be opened (empty or too large)." });
            return new Reply(200, "application/octet-stream", await File.ReadAllBytesAsync(path).ConfigureAwait(false), [("X-Lvz-Name", Uri.EscapeDataString(entry))]);
        }
        return JsonReply(404, new { ok = false, message = $"{entry} is listed but its file is missing." });
    }
}

public sealed partial class ArenaOwnership
{
    /// <summary>The arena's [General] LevelFiles setting. Mainloop.</summary>
    internal string? LevelFilesSetting(ConfigHandle cfg) => _configManager.GetStr(cfg, "General", "LevelFiles");
}
