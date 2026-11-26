using System.Net;
using Dare.Arenas.Core;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// A signed-in pilot's collected maps (/arena/api/library...): every .lvl they drop on the arena
// editor, add, save or download in the map editor, or put on an arena (/api/upload keeps a copy). Store only (thread-safe), so nothing here needs the
// mainloop; a map reaches an arena only through the map editor's Publish (/api/upload).
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private const int MaxMapBytes = 2 * 1024 * 1024;

    private async Task<Reply?> LibraryRouteAsync(HttpListenerRequest req, string path, string method)
    {
        switch (path)
        {
            case "/api/library": return method == "GET" ? LibraryList(req) : MethodNotAllowed();
            case "/api/library/file": return method == "GET" ? LibraryFile(req) : MethodNotAllowed();
            case "/api/library/save": return method == "POST" ? await LibrarySaveAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/library/remove": return method == "POST" ? await LibraryRemoveAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/library/rename": return method == "POST" ? await LibraryRenameAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            default: return null;
        }
    }

    private Reply LibraryList(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        return JsonReply(200, new { ok = true, max = ArenaStore.MaxPilotMaps, maps = _m.WebStore.Maps(player) });
    }

    private Reply LibraryFile(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        if (!long.TryParse(req.QueryString["id"], out var id) || _m.WebStore.MapFile(player, id) is not { } map)
            return JsonReply(404, new { ok = false, message = "That map isn't in your collection." });
        return new Reply(200, "application/octet-stream", map.Data, [("X-Map-Name", Uri.EscapeDataString(map.Name))]);
    }

    /// <summary>POST ?name=&amp;id= with the .lvl bytes: a new map (or the one with the same bytes), or (with id) replaces that one.</summary>
    private async Task<Reply> LibrarySaveAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        if (req.ContentLength64 > MaxMapBytes)
            return JsonReply(413, new { ok = false, message = "Map files must be 2 MB or smaller." });
        var content = await ReadBodyAsync(req, MaxMapBytes, UploadBodyTimeout).ConfigureAwait(false);
        if (content is null)
            return JsonReply(413, new { ok = false, message = "Map files must be 2 MB or smaller." });
        if (!UploadPolicy.TryCheck(UploadKind.Map, "map.lvl", content, 0, out var error))
            return JsonReply(400, new { ok = false, message = error });
        long? id = long.TryParse(req.QueryString["id"], out var n) ? n : null;
        var result = _m.WebStore.SaveMap(player, id, req.QueryString["name"] ?? "map", content, out var savedId);
        return JsonReply(result.Ok ? 200 : 400, new { ok = result.Ok, message = result.Message, id = savedId });
    }

    private async Task<Reply> LibraryRemoveAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (!long.TryParse(Str(body, "id"), out var id))
            return JsonReply(400, new { ok = false, message = "Send {\"id\"}." });
        return ResultReply(_m.WebStore.RemoveMap(player, id));
    }

    /// <summary>POST {"id", "name"}.</summary>
    private async Task<Reply> LibraryRenameAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (!long.TryParse(Str(body, "id"), out var id) || string.IsNullOrWhiteSpace(Str(body, "name")))
            return JsonReply(400, new { ok = false, message = "Send {\"id\", \"name\"}." });
        return ResultReply(_m.WebStore.RenameMap(player, id, Str(body, "name")!));
    }
}
