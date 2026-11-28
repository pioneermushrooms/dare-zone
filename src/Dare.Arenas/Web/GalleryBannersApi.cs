using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// The Foundry's banner library (/account/api/gallery/banners...): banners brought in from the
// SubSpace Banner Emporium, credited to their authors' names as text. A page lists 200 and comes
// with one atlas PNG holding all 200 (a few KB), so a page of banners is two requests, never one
// per banner. Browsing is public; keeping one (into the Armory, optionally as your banner) needs a
// signed-in pilot; staff can take one down. Banners pilots share themselves are ordinary gallery
// items of kind "banner" (GalleryApi.cs).
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    internal const string EmporiumName = "SubSpace Banner Emporium";
    internal const string EmporiumUrl = "https://www.trenchwars.org/SSBE/";

    // Atlases and pages by their filters and the library's revision: the same request is answered from memory.
    private readonly ConcurrentDictionary<string, byte[]> _bannerAtlases = new();
    private const int MaxCachedAtlases = 512;

    private async Task<Reply> BannerLibraryRouteAsync(HttpListenerRequest req, string api)
    {
        var method = req.HttpMethod;
        if (method == "GET")
        {
            if (!_m.GalleryReadPerIp.TryTake("gallery:read:" + ClientIp(req))) return Bad(TooMany, 429);
            return api switch
            {
                "/gallery/banners" => await BannerLibraryListAsync(req).ConfigureAwait(false),
                "/gallery/banners/atlas" => BannerLibraryAtlas(req),
                "/gallery/banners/file" => BannerLibraryFile(req),
                _ => Text(404, "Not found."),
            };
        }
        if (method != "POST") return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused) return refused;
        var (body, error) = await ReadJsonAsync(req, MaxJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var id = Num(body, "id");
        switch (api)
        {
            case "/gallery/banners/keep":
                if (!_m.GallerySavePerAccount.TryTake("gallery:save:" + cur.Account.Id)) return Bad(TooMany, 429);
                var use = IsYes(body, "use");
                var r = _m.Accounts.KeepLibraryBanner(cur.Account.Id, id, use);
                if (r.Ok && use) await ApplyBannerNowAsync(cur.Account).ConfigureAwait(false);
                return ResultReply(r);
            case "/gallery/banners/remove":
                if (!await OnMain(() => _m.IsWebStaff(cur.Account.Name)).ConfigureAwait(false)) return Bad("Only staff can take down a library banner.", 403);
                var gone = _m.Accounts.HideLibraryBanner(id);
                if (gone.Ok) _m.Log(SS.Core.ComponentInterfaces.LogLevel.Info, $"Library banner {id} taken down by staff account {cur.Account.Id}.");
                return ResultReply(gone);
            default:
                return Text(404, "Not found.");
        }
    }

    private static bool IsYes(JsonElement? body, string name) => Str(body, name) is "true" or "1";

    private static (string? Q, string? Author, string Sort, int Page) BannerQuery(HttpListenerRequest req)
    {
        var q = req.QueryString;
        var sort = AccountStore.BannerLibrarySorts.Contains(q["sort"]) ? q["sort"]! : "new";
        var page = int.TryParse(q["page"], out var p) ? Math.Clamp(p, 0, 100_000) : 0;
        string? Short(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 120)];
        return (Short(q["q"]), Short(q["author"]), sort, page);
    }

    /// <summary>?q= &amp;author= &amp;sort=new|old|popular &amp;page=: one page of the library (200 banners) and its atlas's URL.</summary>
    private async Task<Reply> BannerLibraryListAsync(HttpListenerRequest req)
    {
        var (q, author, sort, page) = BannerQuery(req);
        var cur = CurrentAccount(req);
        var staff = cur is { } c && await OnMain(() => _m.IsWebStaff(c.Account.Name)).ConfigureAwait(false);
        var p = _m.Accounts.BannerLibrary(q, author, sort, page);
        var args = new List<string>();
        if (q is not null) args.Add("q=" + Uri.EscapeDataString(q));
        if (author is not null) args.Add("author=" + Uri.EscapeDataString(author));
        args.Add("sort=" + sort);
        args.Add("page=" + p.Page);
        args.Add("v=" + p.Revision);
        return JsonReply(200, new
        {
            ok = true,
            count = p.Count,
            page = p.Page,
            per = p.PerPage,
            pages = (p.Count + p.PerPage - 1) / p.PerPage,
            cols = AccountStore.BannerLibraryColumns,
            cell = new { width = BannerFile.Width, height = BannerFile.Height },
            atlas = p.Items.Count == 0 ? null : $"{AccountPrefix}/api/gallery/banners/atlas?{string.Join('&', args)}",
            items = p.Items.Select(b => new
            {
                id = b.Id,
                author = b.Author,
                added = b.Added,
                players = b.Players,
                saves = b.Saves,
                source = b.Source == AccountStore.EmporiumSource ? $"{EmporiumUrl}?action=banner&id={b.SourceId}" : null,
                file = $"{AccountPrefix}/api/gallery/banners/file?id={b.Id}",
            }),
            credit = new { name = EmporiumName, url = EmporiumUrl },
            viewer = cur is { } v ? new { name = v.Account.Name, staff } : null,
        });
    }

    /// <summary>The atlas PNG for the same filters and page as the list (its URL carries the revision, so it can be kept for good).</summary>
    private Reply BannerLibraryAtlas(HttpListenerRequest req)
    {
        var (q, author, sort, page) = BannerQuery(req);
        var rev = _m.Accounts.BannerLibraryRevision();
        var key = $"{rev}|{sort}|{page}|{q}|{author}";
        if (!_bannerAtlases.TryGetValue(key, out var png))
        {
            png = _m.Accounts.BannerLibraryAtlas(q, author, sort, page);
            if (_bannerAtlases.Count >= MaxCachedAtlases) _bannerAtlases.Clear();
            _bannerAtlases[key] = png;
        }
        var current = req.QueryString["v"] == rev;
        return new Reply(200, "image/png", png,
        [
            ("Cache-Control", current ? "public, max-age=31536000, immutable" : "public, max-age=60"),
            ("X-Content-Type-Options", "nosniff"),
        ]);
    }

    /// <summary>?id= [&amp;download=1]: one library banner as the game's own 12×8 BMP.</summary>
    private Reply BannerLibraryFile(HttpListenerRequest req)
    {
        if (!long.TryParse(req.QueryString["id"], out var id)) return Text(400, "Bad id.");
        if (_m.Accounts.LibraryBannerFile(id) is not { } b) return Text(404, "Not found.");
        var headers = new List<(string, string)> { ("Cache-Control", "public, max-age=86400"), ("X-Content-Type-Options", "nosniff") };
        if (req.QueryString["download"] == "1")
        {
            var stem = new string(b.Banner.Author.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            headers.Add(("Content-Disposition", $"attachment; filename=\"banner-{(stem.Length == 0 ? "" : stem + "-")}{b.Banner.SourceId}.bmp\""));
        }
        return new Reply(200, "image/bmp", b.File, [.. headers]);
    }
}
