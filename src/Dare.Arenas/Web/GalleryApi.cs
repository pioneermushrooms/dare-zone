using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;

namespace Dare.Arenas;

public sealed partial class ArenaOwnership
{
    // The shared gallery: contributions are scarcer than saves; browsing is cheap but bounded per address.
    internal readonly RateLimiter GalleryAddPerAccount = new(20, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter GalleryAddPerIp = new(40, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter GallerySavePerAccount = new(120, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter GalleryDetectPerIp = new(120, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter GalleryReadPerIp = new(600, TimeSpan.FromMinutes(1), Clock);
}

// ---------------------------------------------------------------------------------------------
// The shared gallery (/account/api/gallery...): ships and graphics pilots contribute for everyone.
// Browsing and pictures are public (signed out too); contributing, keeping a copy and taking one
// down need a signed-in pilot. A contribution is credited to the one pilot name that shared it,
// and nothing else about them is ever returned.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private async Task<Reply> GalleryRouteAsync(HttpListenerRequest req, string api)
    {
        var method = req.HttpMethod;
        if (api == "/gallery")
            return method == "GET" ? await GalleryListAsync(req).ConfigureAwait(false) : MethodNotAllowed();
        if (api == "/gallery/image")
            return method == "GET" ? GalleryImage(req) : MethodNotAllowed();
        if (api == "/gallery/thumb")
            return method == "GET" ? GalleryThumb(req) : MethodNotAllowed();
        if (method != "POST") return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused) return refused;
        var big = api is "/gallery/add" or "/gallery/detect";
        var (body, error) = await ReadJsonAsync(req, big ? MaxGalleryJsonBytes : MaxJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;
        if (api == "/gallery/detect") return GalleryDetect(req, body);
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var id = cur.Account.Id;
        switch (api)
        {
            case "/gallery/add":
                return GalleryAdd(req, id, body);
            case "/gallery/save":
                if (!_m.GallerySavePerAccount.TryTake("gallery:save:" + id)) return Bad(TooMany, 429);
                if (KeepInLibrary(cur.Account.Name, Num(body, "id")) is { } kept) return kept;
                return ResultReply(_m.Accounts.SaveGalleryItem(id, Num(body, "id")));
            case "/gallery/remove":
                var staff = await OnMain(() => _m.IsWebStaff(cur.Account.Name)).ConfigureAwait(false);
                var r = _m.Accounts.RemoveGalleryItem(id, Num(body, "id"), staff);
                if (r.Ok) _m.Log(SS.Core.ComponentInterfaces.LogLevel.Info, $"Gallery item {Num(body, "id")} taken down by account {id}{(staff ? " (staff)" : "")}.");
                return ResultReply(r);
            default:
                return Text(404, "Not found.");
        }
    }

    // a map (up to 1 MB) and its preview, as base64
    private const int MaxGalleryJsonBytes = (LvlFile.MaxGalleryBytes + AccountStore.MaxGalleryThumbBytes) / 3 * 4 + 16 * 1024;

    private static object KindJson(GalleryKind k) => new
    {
        name = k.Name, label = k.Label, group = k.Group, width = k.Width, height = k.Height, save = k.Save,
        file = AccountStore.FindGalleryFileKind(k.Name) is { } f ? f.Extension : null,
    };

    /// <summary>
    /// Keep for maps and tilesets: into the pilot's own maps or tilesets, the Arena Editor's library
    /// (the map editor opens and applies them from there). Null for every other kind.
    /// </summary>
    private Reply? KeepInLibrary(string pilot, long itemId)
    {
        if (_m.Accounts.GalleryImage(itemId) is not { } item) return null;
        var save = AccountStore.FindGalleryKind(item.Kind)?.Save;
        if (save is not ("maps" or "tilesets")) return null;
        Result r;
        long savedId;
        if (save == "maps") r = _m.WebStore.SaveMap(pilot, null, item.Name, item.Image, out savedId);
        else r = _m.WebStore.SaveTileset(pilot, null, item.Name, item.Image, null, out savedId);
        if (r.Ok) _m.Accounts.CountGallerySave(itemId);
        return JsonReply(r.Ok ? 200 : 400, new { ok = r.Ok, message = r.Message, kept = r.Ok ? save : null, keptId = savedId });
    }

    /// <summary>?kind= &amp;ship= &amp;q= &amp;pilot= &amp;before= &amp;limit=: a page of designs, newest first, and the kinds there are.</summary>
    private async Task<Reply> GalleryListAsync(HttpListenerRequest req)
    {
        if (!_m.GalleryReadPerIp.TryTake("gallery:read:" + ClientIp(req))) return Bad(TooMany, 429);
        var q = req.QueryString;
        var cur = CurrentAccount(req);
        var staff = cur is { } c && await OnMain(() => _m.IsWebStaff(c.Account.Name)).ConfigureAwait(false);
        var page = _m.Accounts.Gallery(cur?.Account.Id ?? 0, q["kind"], int.TryParse(q["ship"], out var s) ? s : 0, q["q"], q["pilot"],
            long.TryParse(q["before"], out var before) ? before : 0, int.TryParse(q["limit"], out var limit) ? limit : 30);
        return JsonReply(200, new
        {
            ok = true,
            count = page.Count,
            next = page.Next,
            items = page.Items.Select(i => new
            {
                id = i.Id,
                kind = i.Kind,
                kindLabel = AccountStore.FindGalleryKind(i.Kind)?.Label ?? i.Kind,
                ship = i.Ship,
                name = i.Name,
                keywords = i.Keywords.Length == 0 ? [] : i.Keywords.Split(' '),
                prompt = i.Prompt,
                source = i.Source,
                by = i.By,
                mine = i.Mine,
                created = i.Created,
                saves = i.Saves,
                // a picture to show: a map's preview (built when it was shared), else the design itself
                image = AccountStore.FindGalleryFileKind(i.Kind) is { ThumbWidth: > 0 }
                    ? $"{AccountPrefix}/api/gallery/thumb?id={i.Id}" : $"{AccountPrefix}/api/gallery/image?id={i.Id}",
                file = $"{AccountPrefix}/api/gallery/image?id={i.Id}",
            }),
            kinds = AccountStore.GalleryKinds.Select(KindJson),
            viewer = cur is { } v ? new { name = v.Account.Name, staff } : null,
        });
    }

    /// <summary>?id=: a design's picture (public; it never changes). &amp;download=1 saves it as a file.</summary>
    private Reply GalleryImage(HttpListenerRequest req)
    {
        if (!long.TryParse(req.QueryString["id"], out var id)) return Text(400, "Bad id.");
        if (_m.Accounts.GalleryImage(id) is not { } item) return Text(404, "Not found.");
        var headers = new List<(string, string)> { ("Cache-Control", "public, max-age=86400"), ("X-Content-Type-Options", "nosniff") };
        if (req.QueryString["download"] == "1")
        {
            var stem = new string(item.Name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            var ext = AccountStore.FindGalleryFileKind(item.Kind)?.Extension ?? (ImageType(item.Image) == "image/png" ? "png" : "bmp");
            headers.Add(("Content-Disposition", $"attachment; filename=\"{item.Kind}-{(stem.Length == 0 ? id.ToString() : stem)}.{ext}\""));
        }
        var type = AccountStore.FindGalleryFileKind(item.Kind)?.ContentType ?? ImageType(item.Image);
        return new Reply(200, type, item.Image, [.. headers]);
    }

    /// <summary>?id=: a map's preview PNG, built once by the pilot who shared it (404 when it has none).</summary>
    private Reply GalleryThumb(HttpListenerRequest req)
    {
        if (!long.TryParse(req.QueryString["id"], out var id)) return Text(400, "Bad id.");
        if (_m.Accounts.GalleryThumb(id) is not { } png) return Text(404, "Not found.");
        return new Reply(200, "image/png", png, [("Cache-Control", "public, max-age=86400"), ("X-Content-Type-Options", "nosniff")]);
    }

    /// <summary>{"sheet"}: which kinds a picture can be, by its size, before it is shared.</summary>
    private Reply GalleryDetect(HttpListenerRequest req, JsonElement? body)
    {
        if (!_m.GalleryDetectPerIp.TryTake("gallery:detect:" + ClientIp(req))) return Bad(TooMany, 429);
        byte[] image;
        try { image = Convert.FromBase64String(Str(body, "sheet") ?? ""); }
        catch (FormatException) { return Bad("That isn't a picture or a map."); }
        var matches = AccountStore.DetectGalleryKinds(image, out var w, out var h, out var why);
        if (matches.Count == 0) return JsonReply(400, new { ok = false, message = why, width = w, height = h, matches = Array.Empty<object>() });
        return JsonReply(200, new { ok = true, width = w, height = h, matches = matches.Select(m => KindJson(AccountStore.FindGalleryKind(m)!)) });
    }

    /// <summary>
    /// {"sheet"} (an upload) or {"from": "armory"|"shipyard", "id"} (one of the pilot's own), plus
    /// "kind"? "ship"? "name"? "keywords"?. When the size fits several kinds and none was chosen, the
    /// answer is 409 with "choices".
    /// </summary>
    private Reply GalleryAdd(HttpListenerRequest req, long accountId, JsonElement? body)
    {
        if (!_m.GalleryAddPerAccount.TryTake("gallery:add:" + accountId)) return Bad("You've shared a lot this hour. Come back later.", 429);
        if (!_m.GalleryAddPerIp.TryTake("gallery:add:ip:" + ClientIp(req))) return Bad(TooMany, 429);
        var ship = (int)Math.Max(0, Num(body, "ship"));
        GalleryAddResult r;
        if (Str(body, "from") is { Length: > 0 } from)
        {
            r = _m.Accounts.AddGalleryFromSaved(accountId, from, Num(body, "id"), Str(body, "kind"), ship, Str(body, "name"), Str(body, "keywords"));
        }
        else
        {
            byte[] image, thumb;
            try { image = Convert.FromBase64String(Str(body, "sheet") ?? ""); thumb = Convert.FromBase64String(Str(body, "thumb") ?? ""); }
            catch (FormatException) { return Bad("That isn't a picture or a map."); }
            r = _m.Accounts.AddGalleryItem(accountId, Str(body, "kind"), ship, Str(body, "name"), Str(body, "keywords"), null, "uploaded", image, thumb);
        }
        if (r.Ok) return JsonReply(200, new { ok = true, message = r.Message, id = r.Id });
        var choices = r.Choices.Select(m => KindJson(AccountStore.FindGalleryKind(m)!)).ToArray();
        return JsonReply(choices.Length > 1 ? 409 : 400, new { ok = false, message = r.Message, choices });
    }
}
