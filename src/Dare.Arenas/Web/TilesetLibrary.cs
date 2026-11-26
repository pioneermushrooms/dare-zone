using System.Net;
using Dare.Arenas.Core;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// A signed-in pilot's saved tilesets (/arena/api/tilesets...): the tile editor's library, each a
// tileset BMP plus its role map (docs/tile-layout.md). Store only (thread-safe), so nothing here
// needs the mainloop. A tileset reaches an arena only inside a map: the tile editor puts it into a
// map in the pilot's maps, and the map is published from there like any other.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private const int MaxTilesetJsonBytes = 256 * 1024; // base64 BMP (<= 128 KB) + role map (<= 64 KB)

    private async Task<Reply?> TilesetRouteAsync(HttpListenerRequest req, string path, string method)
    {
        switch (path)
        {
            case "/api/tilesets": return method == "GET" ? TilesetList(req) : MethodNotAllowed();
            case "/api/tilesets/file": return method == "GET" ? TilesetBmp(req) : MethodNotAllowed();
            case "/api/tilesets/roles": return method == "GET" ? TilesetRoles(req) : MethodNotAllowed();
            case "/api/tilesets/save": return method == "POST" ? await TilesetSaveAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/tilesets/remove":
            case "/api/tilesets/rename":
            case "/api/tilesets/duplicate":
                return method == "POST" ? await TilesetActionAsync(req, path).ConfigureAwait(false) : MethodNotAllowed();
            default: return null;
        }
    }

    private Reply TilesetList(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        return JsonReply(200, new { ok = true, max = ArenaStore.MaxPilotTilesets, tilesets = _m.WebStore.Tilesets(player) });
    }

    private Reply TilesetBmp(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        if (!long.TryParse(req.QueryString["id"], out var id) || _m.WebStore.TilesetFileOf(player, id) is not { } t)
            return JsonReply(404, new { ok = false, message = "That tileset isn't in your collection." });
        return new Reply(200, "image/bmp", t.Bmp, [("X-Tileset-Name", Uri.EscapeDataString(t.Name))]);
    }

    private Reply TilesetRoles(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        if (!long.TryParse(req.QueryString["id"], out var id) || _m.WebStore.TilesetFileOf(player, id) is not { } t)
            return JsonReply(404, new { ok = false, message = "That tileset isn't in your collection." });
        if (t.Roles is null) return JsonReply(404, new { ok = false, message = "This tileset has no role map." });
        return new Reply(200, "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(t.Roles));
    }

    /// <summary>POST ?name=&amp;id= with {"bmp": base64, "roles": json text or null}: a new tileset, or (with id) replaces that one.</summary>
    private async Task<Reply> TilesetSaveAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req, MaxTilesetJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;
        byte[] bmp;
        try { bmp = Convert.FromBase64String(Str(body, "bmp") ?? ""); }
        catch (FormatException) { return JsonReply(400, new { ok = false, message = "Send the tileset as base64 in \"bmp\"." }); }
        if (!TilesetFile.TryCheckBmp(bmp, out var bad)) return JsonReply(400, new { ok = false, message = bad });
        var roles = Str(body, "roles");
        if (!TilesetFile.TryCheckRoles(roles, out bad)) return JsonReply(400, new { ok = false, message = bad });
        long? id = long.TryParse(req.QueryString["id"], out var n) ? n : null;
        var result = _m.WebStore.SaveTileset(player, id, req.QueryString["name"] ?? "tileset", bmp, roles, out var savedId);
        return JsonReply(result.Ok ? 200 : 400, new { ok = result.Ok, message = result.Message, id = savedId });
    }

    /// <summary>POST {"id"} to remove or duplicate, {"id", "name"} to rename.</summary>
    private async Task<Reply> TilesetActionAsync(HttpListenerRequest req, string path)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (!long.TryParse(Str(body, "id"), out var id))
            return JsonReply(400, new { ok = false, message = "Send {\"id\"}." });
        switch (path)
        {
            case "/api/tilesets/remove": return ResultReply(_m.WebStore.RemoveTileset(player, id));
            case "/api/tilesets/rename":
                if (string.IsNullOrWhiteSpace(Str(body, "name"))) return JsonReply(400, new { ok = false, message = "Send {\"id\", \"name\"}." });
                return ResultReply(_m.WebStore.RenameTileset(player, id, Str(body, "name")!));
            default:
                var r = _m.WebStore.DuplicateTileset(player, id, out var copy);
                return JsonReply(r.Ok ? 200 : 400, new { ok = r.Ok, message = r.Message, id = copy });
        }
    }
}
