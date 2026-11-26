using System.Net;
using System.Reflection;
using System.Text.Json;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// The arena editor's Graphics page (/arena/api/graphics...): the game's replaceable graphics, the
// arena's current picture for each (its own, another .lvz's, or the stock one), uploads, resets,
// and new pictures from the ship service's generators (previewed first; nothing is saved until
// the owner uses one).
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private static readonly Dictionary<string, byte[]> StockGraphics = LoadStockGraphics();

    private static Dictionary<string, byte[]> LoadStockGraphics()
    {
        var asm = Assembly.GetExecutingAssembly();
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("gfx/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[Path.GetFileNameWithoutExtension(res[4..])] = ms.ToArray();
        }
        return map;
    }

    /// <summary>The stock picture: ship1..ship8 share ships.bm2 (a 144-pixel band each, cut out by the page).</summary>
    private static byte[]? Stock(string slot) =>
        StockGraphics.GetValueOrDefault(GameGraphics.ShipIndex(slot) > 0 ? "ships" : slot);

    private static string ImageType(byte[] b) => b.Length > 4 && b[0] == 0x89 && b[1] == (byte)'P' ? "image/png" : "image/bmp";

    private async Task<Reply?> GraphicsRouteAsync(HttpListenerRequest req, string path, string method)
    {
        switch (path)
        {
            case "/api/graphics": return method == "GET" ? await GraphicsListAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/graphics/image": return method == "GET" ? await GraphicImageAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/graphics/set": return method == "POST" ? await GraphicSetAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/graphics/clear":
                return method == "POST" ? await OwnerConfigActionAsync(req, (cfg, p, a, b) => _m.ClearGraphic(cfg, a, p, Str(b, "name") ?? "")).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/graphics/generate": return method == "POST" ? await GraphicGenerateAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            default: return null;
        }
    }

    /// <summary>Owner or co-mod of ?arena=: the picked arena, or a refusal.</summary>
    private Reply? PickArena(HttpListenerRequest req, string player, string? wanted, bool ownerOnly, out string arena)
    {
        arena = "";
        var pick = Pick(_m.WebStore.RolesFor(player), wanted);
        if (pick is null || (ownerOnly && pick.Value.Role != "owner"))
            return JsonReply(403, new { ok = false, message = ownerOnly ? "Only the arena's owner can change its graphics." : "You are not an owner or co-mod of that arena." });
        arena = pick.Value.Arena;
        return null;
    }

    private async Task<T?> WithConfigAsync<T>(string arena, Func<SS.Core.ConfigHandle, T?> work) where T : class
    {
        var cfg = await _m.OpenArenaConfigAsync(arena).ConfigureAwait(false);
        if (cfg is null) return null;
        try { return await OnMain(() => work(cfg)).ConfigureAwait(false); }
        finally { _m.CloseArenaConfig(cfg); }
    }

    private async Task<Reply> GraphicsListAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        if (PickArena(req, player, req.QueryString["arena"], ownerOnly: false, out var arena) is { } refused) return refused;
        var data = await WithConfigAsync(arena, cfg => _m.GraphicsData(cfg, arena)).ConfigureAwait(false);
        return data is null
            ? JsonReply(409, new { ok = false, message = "Your arena's settings file is missing; ask staff." })
            : JsonReply(200, new { ok = true, arena, graphics = data });
    }

    private async Task<Reply> GraphicImageAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out var player) is { } denied) return denied;
        var name = req.QueryString["name"] ?? "";
        if (GameGraphics.Find(name) is not { } slot) return Text(404, "Not found.");
        if (req.QueryString["stock"] == "1")
            return Stock(slot.Name) is { } stock ? new Reply(200, ImageType(stock), stock) : Text(404, "Not found.");
        if (PickArena(req, player, req.QueryString["arena"], ownerOnly: false, out var arena) is { } refused) return refused;
        var own = await WithConfigAsync(arena, cfg => _m.GraphicImage(cfg, arena, slot.Name)).ConfigureAwait(false);
        var bytes = own ?? Stock(slot.Name);
        if (bytes is null) return Text(404, "Not found.");
        return new Reply(200, ImageType(bytes), bytes, [("X-Graphic-Source", own is null ? "stock" : "arena"), ("Cache-Control", "no-store")]);
    }

    /// <summary>POST ?arena=&amp;name= with the PNG/BMP bytes.</summary>
    private async Task<Reply> GraphicSetAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        if (PickArena(req, player, req.QueryString["arena"], ownerOnly: true, out var arena) is { } refused) return refused;
        if (req.ContentLength64 > GameGraphics.MaxImageBytes)
            return JsonReply(413, new { ok = false, message = "Images must be 1 MB or smaller." });
        var image = await ReadBodyAsync(req, GameGraphics.MaxImageBytes, UploadBodyTimeout).ConfigureAwait(false);
        if (image is null) return JsonReply(413, new { ok = false, message = "Images must be 1 MB or smaller." });
        var name = req.QueryString["name"] ?? "";
        var result = await WithConfigAsync(arena, cfg => _m.SetGraphic(cfg, arena, player, name, image)).ConfigureAwait(false);
        return result is null ? JsonReply(409, new { ok = false, message = "Your arena's settings file is missing; ask staff." }) : ResultReply(result);
    }

    private async Task<Reply> OwnerConfigActionAsync(HttpListenerRequest req, Func<SS.Core.ConfigHandle, string, string, JsonElement, Result> act)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (PickArena(req, player, Str(body, "arena"), ownerOnly: true, out var arena) is { } refused) return refused;
        var json = body.Value;
        var result = await WithConfigAsync(arena, cfg => act(cfg, player, arena, json)).ConfigureAwait(false);
        return result is null ? JsonReply(409, new { ok = false, message = "Your arena's settings file is missing; ask staff." }) : ResultReply(result);
    }

    /// <summary>
    /// {"arena", "name", "mode", "prompt"?}: a new picture from the ship service, not saved. Ships use the
    /// ship generator ("ship" = random, "ship-prompt"); other graphics send the arena's current picture as
    /// the template the generator keeps the size and frame layout of ("recolor", "new", "prompt").
    /// </summary>
    private async Task<Reply> GraphicGenerateAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (PickArena(req, player, Str(body, "arena"), ownerOnly: true, out var arena) is { } refused) return refused;
        if (GameGraphics.Find(Str(body, "name")) is not { } slot) return Bad("That isn't one of the game's graphics.");
        var mode = Str(body, "mode") ?? "";
        if (!slot.Generate.Contains(mode)) return Bad($"{slot.Label} can't be generated that way.");
        var prompt = (Str(body, "prompt") ?? "").Trim();
        var prompted = mode is "prompt" or "ship-prompt";
        if (prompted)
        {
            if (prompt.Length == 0) return Bad("Describe what you want.");
            if (prompt.Length > 500) return Bad("Keep the description under 501 characters.");
            if (!_m.ShipPromptPerAccount.TryTake("gfx:prompt:" + player)) return Bad("You've drawn a lot this hour. Try a random one, or come back later.", 429);
            if (!_m.ShipPromptPerIp.TryTake("ship:prompt:ip:" + ClientIp(req))) return Bad(TooMany, 429);
        }
        else if (!_m.ShipRandomPerAccount.TryTake("gfx:random:" + player)) return Bad(TooMany, 429);

        string endpoint;
        object request;
        if (GameGraphics.ShipIndex(slot.Name) > 0)
        {
            endpoint = "/generate";
            request = prompted ? new { mode = "prompt", prompt, creative = false, variation = false } : new { mode = "random" };
        }
        else
        {
            var current = await WithConfigAsync(arena, cfg => _m.GraphicImage(cfg, arena, slot.Name)).ConfigureAwait(false) ?? Stock(slot.Name);
            if (current is null) return Bad("There's no picture to start from.");
            endpoint = "/graphic";
            request = new { kind = slot.Name, mode, prompt = prompted ? prompt : null, template = Convert.ToBase64String(current) };
        }

        try
        {
            using var content = JsonContent(request);
            using var res = await ShipService.PostAsync(ShipServiceUrl + endpoint, content).ConfigureAwait(false);
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var status = (int)res.StatusCode;
            _m.Log(status == 200 ? LogLevel.Info : LogLevel.Warn, $"Graphic {slot.Name} {mode} for {player} in [{arena}]: {status}.");
            return JsonBytes(status == 200 ? 200 : status is 400 or 429 or 503 ? status : 502, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _m.Log(LogLevel.Warn, $"Ship service unreachable at {ShipServiceUrl}: {ex.GetType().Name}.");
            return Bad("The generator is offline right now. Try again later.", 503);
        }
    }
}
