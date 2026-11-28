using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;

namespace Dare.Arenas;

public sealed partial class ArenaOwnership
{
    /// <summary>The public arenas <paramref name="name"/> owns (private ones and archived ones left out), by name. Store reads only.</summary>
    internal IReadOnlyList<string> ProfileArenas(string name)
    {
        var hidden = Store.PrivateArenas();
        return Store.OwnedArenas(name)
            .Where(a => a.Status != ArenaStatus.Archived && !hidden.Contains(a.Name) && !DraftRules.IsDraft(a.Name))
            .Select(a => a.Name).ToList();
    }
}

// ---------------------------------------------------------------------------------------------
// Public pilot profiles: the page at /pilot/<name> and its data under /account/api/profile.
// Reads are public (signed out too) and never carry email, sign-in times, ids or the pilot's
// other names. Only the signed-in owner of that exact name saves; zone staff moderate.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    internal const string PilotPrefix = "/pilot";
    private const string NoSuchPilot = "No pilot by that name.";

    private static bool IsPilotPath(string path) => path == PilotPrefix || path.StartsWith(PilotPrefix + "/", StringComparison.Ordinal);

    /// <summary>/pilot/&lt;name&gt; is the profile page (the page reads the name from its own URL); /pilot alone has none.</summary>
    private Reply PilotRoute(HttpListenerRequest req, string path)
    {
        if (path.Length <= PilotPrefix.Length + 1) return Text(404, "Not found.");
        return Get(req.HttpMethod, () => Static("profile.html"));
    }

    /// <summary>/account/profile: the signed-in pilot's own profile, or sign in first and come back.</summary>
    private Reply MyProfileRedirect(HttpListenerRequest req)
    {
        var to = CurrentAccount(req)?.Account is { } me
            ? PilotPrefix + "/" + Uri.EscapeDataString(me.Name)
            : AccountPrefix + "/login?next=" + Uri.EscapeDataString(AccountPrefix + "/profile");
        return new Reply(302, "text/plain; charset=utf-8", [], [("Location", to)]);
    }

    private async Task<Reply> ProfileRouteAsync(HttpListenerRequest req, string api)
    {
        var method = req.HttpMethod;
        if (api == "/profile" && method is "GET" or "HEAD") return await ProfileGetAsync(req).ConfigureAwait(false);
        if (api == "/profile/ship") return Get(method, () => ProfileShip(req));
        if (api == "/profile/wall" && method is "GET" or "HEAD") return await WallGetAsync(req).ConfigureAwait(false);
        if (method != "POST") return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused) return refused;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        return api switch
        {
            "/profile" => ProfileSave(cur.Account, body),
            "/profile/moderate" => await ProfileModerateAsync(cur.Account, body).ConfigureAwait(false),
            "/profile/wall" => WallLeave(cur.Account, body),
            "/profile/wall/remove" => await WallRemoveAsync(cur.Account, body).ConfigureAwait(false),
            _ => Text(404, "Not found."),
        };
    }

    private async Task<Reply> ProfileGetAsync(HttpListenerRequest req)
    {
        var name = (req.QueryString["name"] ?? "").Trim();
        var me = CurrentAccount(req)?.Account;
        var mine = me is not null && string.Equals(me.Name, name, StringComparison.OrdinalIgnoreCase);
        var staff = me is not null && await OnMain(() => _m.IsWebStaff(me.Name)).ConfigureAwait(false);
        var p = _m.Accounts.PublicPilot(name, privileged: mine || staff);
        if (p is null) return JsonReply(404, new { ok = false, message = NoSuchPilot });
        var arenas = p.ShowArenas || mine ? _m.ProfileArenas(p.Name) : [];
        return JsonReply(200, new
        {
            ok = true,
            name = p.Name,
            since = p.Since,
            bio = p.Bio,
            avatar = p.Avatar,
            show = new { contribs = p.ShowContribs, arenas = p.ShowArenas, ships = p.ShowShips },
            arenas,
            ships = p.Ships.Select(s => new { s.Id, s.Name }),
            hidden = p.Hidden,
            mine,
            staff,
            // the owner's edit form: every Shipyard ship to pick an avatar from
            own = mine ? _m.Accounts.Shipyard(me!.Id).Ships.Select(s => new { s.Id, s.Name }) : null,
        });
    }

    private Reply ProfileShip(HttpListenerRequest req)
    {
        var name = req.QueryString["name"] ?? "";
        if (!long.TryParse(req.QueryString["id"], out var id)) return Text(404, "Not found.");
        var png = _m.Accounts.ProfileShipSheet(name, id);
        // the owner may also see any of their own ships (the avatar picker)
        if (png is null && CurrentAccount(req)?.Account is { } me && string.Equals(me.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            png = _m.Accounts.ShipyardSheet(me.Id, id);
        return png is null ? Text(404, "Not found.") : new Reply(200, "image/png", png, [("Cache-Control", "public, max-age=300")]);
    }

    private Reply ProfileSave(Account me, JsonElement? body)
    {
        var avatar = Num(body, "avatar");
        return ResultReply(_m.Accounts.SaveProfile(me.Id, Str(body, "bio"), avatar > 0 ? avatar : null,
            Flag(body, "showContribs"), Flag(body, "showArenas"), Flag(body, "showShips")));
    }

    private async Task<Reply> ProfileModerateAsync(Account me, JsonElement? body)
    {
        if (!await OnMain(() => _m.IsWebStaff(me.Name)).ConfigureAwait(false))
            return JsonReply(403, new { ok = false, message = "Only zone staff can do that." });
        var name = Str(body, "name") ?? "";
        bool? clear = Str(body, "clearBio") == "true" ? true : null;
        bool? hidden = Str(body, "hidden") switch { "true" => true, "false" => false, _ => null };
        var r = _m.Accounts.ModerateProfile(name, clear, hidden);
        if (r.Ok) _m.Log(SS.Core.ComponentInterfaces.LogLevel.Info, $"Profile moderation by {me.Name}: {r.Message}");
        return ResultReply(r);
    }

    // ---- the wall: dated messages other pilots leave on a profile ------------------------------------------

    /// <summary>GET /profile/wall?name=&amp;before=: the newest messages (20 at a time), and which of them the viewer may remove.</summary>
    private async Task<Reply> WallGetAsync(HttpListenerRequest req)
    {
        var name = (req.QueryString["name"] ?? "").Trim();
        var me = CurrentAccount(req)?.Account;
        var mine = me is not null && string.Equals(me.Name, name, StringComparison.OrdinalIgnoreCase);
        var staff = me is not null && await OnMain(() => _m.IsWebStaff(me.Name)).ConfigureAwait(false);
        long? before = long.TryParse(req.QueryString["before"], out var b) ? b : null;
        var posts = _m.Accounts.WallPosts(name, privileged: mine || staff, before, 20);
        if (posts is null) return JsonReply(404, new { ok = false, message = NoSuchPilot });
        return JsonReply(200, new
        {
            ok = true,
            posts = posts.Select(p => WallJson(p, me, mine, staff)),
            more = posts.Count == 20,
            canPost = me is not null && !mine,
            max = AccountStore.MaxWallLength,
        });
    }

    private static object WallJson(WallPost p, Account? me, bool mine, bool staff) => new
    {
        id = p.Id,
        author = p.Author,
        text = p.Text,
        at = p.At,
        canRemove = mine || staff || (me is not null && string.Equals(me.Name, p.Author, StringComparison.OrdinalIgnoreCase)),
    };

    private Reply WallLeave(Account me, JsonElement? body)
    {
        var name = (Str(body, "name") ?? "").Trim();
        if (string.Equals(me.Name, name, StringComparison.OrdinalIgnoreCase))
            return JsonReply(400, new { ok = false, message = "That's your own wall: others leave messages here." });
        var r = _m.Accounts.PostOnWall(me.Id, name, Str(body, "text"), out var post);
        if (!r.Ok) return ResultReply(r);
        return JsonReply(200, new { ok = true, message = r.Message, post = WallJson(post!, me, false, false) });
    }

    private async Task<Reply> WallRemoveAsync(Account me, JsonElement? body)
    {
        var staff = await OnMain(() => _m.IsWebStaff(me.Name)).ConfigureAwait(false);
        var r = _m.Accounts.RemoveWallPost(me.Id, Num(body, "id"), staff);
        if (r.Ok && staff) _m.Log(SS.Core.ComponentInterfaces.LogLevel.Info, $"Wall message {Num(body, "id")} removed by {me.Name}");
        return ResultReply(r);
    }

    /// <summary>A switch in the body: true unless it says false (a missing switch keeps the default, shown).</summary>
    private static bool Flag(JsonElement? body, string name) => Str(body, name) != "false";
}
