using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// The site inbox, under /account/api: notifications (wall messages, replies, @mentions), direct
// messages between pilots, and the name lookup the @mention and To fields use. Signed-in only;
// the page is /account/inbox.
//   GET  /inbox                  notifications and conversations
//   GET  /inbox/count?after=<id> unread counts, plus notifications newer than <id> (the header's pop-ups)
//   POST /inbox/read             marks the notifications read
//   GET  /messages?with=<name>&before=<id>   one conversation (marks it read)
//   POST /messages {to, text}    send one
//   GET  /names?q=<prefix>       pilot names for @mentions and To
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private static bool IsInboxApi(string api) =>
        api == "/inbox" || api.StartsWith("/inbox/", StringComparison.Ordinal) || api == "/messages" || api == "/names";

    private async Task<Reply> InboxRouteAsync(HttpListenerRequest req, string api)
    {
        var method = req.HttpMethod;
        if (method is "GET" or "HEAD")
        {
            if (CurrentAccount(req) is not { } me) return NotSignedInReply();
            return api switch
            {
                "/inbox" => InboxGet(me.Account),
                "/inbox/count" => InboxCount(me.Account, req),
                "/messages" => MessagesGet(me.Account, req),
                "/names" => JsonReply(200, new { ok = true, names = _m.Accounts.SearchNames(req.QueryString["q"] ?? "") }),
                _ => Text(404, "Not found."),
            };
        }
        if (method != "POST") return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused) return refused;
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        switch (api)
        {
            case "/inbox/read":
                _m.Accounts.MarkNotificationsRead(cur.Account.Id);
                return JsonReply(200, new { ok = true });
            case "/messages":
                var r = _m.Accounts.SendMessage(cur.Account.Id, Str(body, "to") ?? "", Str(body, "text"), out var sent);
                return r.Ok ? JsonReply(200, new { ok = true, message = r.Message, sent = MessageJson(sent!, cur.Account) }) : ResultReply(r);
            default:
                return Text(404, "Not found.");
        }
    }

    private Reply InboxGet(Account me)
    {
        var (notes, msgs, latest) = _m.Accounts.InboxCounts(me.Id);
        return JsonReply(200, new
        {
            ok = true,
            counts = new { notes, messages = msgs, latest },
            notes = _m.Accounts.Notifications(me.Id, 40).Select(NoteJson),
            conversations = _m.Accounts.Conversations(me.Id).Select(c => new { with = c.With, last = c.LastText, at = c.LastAt, fromMe = c.LastFromMe, unread = c.Unread }),
        });
    }

    private Reply InboxCount(Account me, HttpListenerRequest req)
    {
        var (notes, msgs, latest) = _m.Accounts.InboxCounts(me.Id);
        // the header asks for what's new since it last looked, to pop it up
        var fresh = long.TryParse(req.QueryString["after"], out var after) && after > 0 && after < latest
            ? _m.Accounts.Notifications(me.Id, 3, after).Where(n => !n.Read).Select(NoteJson).ToList()
            : [];
        return JsonReply(200, new { ok = true, notes, messages = msgs, latest, fresh });
    }

    private static object NoteJson(InboxNote n) => new
    {
        id = n.Id, kind = n.Kind, actor = n.Actor, wall = n.Wall, post = n.PostId, text = n.Text, at = n.At, read = n.Read,
    };

    private Reply MessagesGet(Account me, HttpListenerRequest req)
    {
        var with = (req.QueryString["with"] ?? "").Trim();
        long? before = long.TryParse(req.QueryString["before"], out var b) ? b : null;
        var list = _m.Accounts.Thread(me.Id, with, before);
        if (list is null) return JsonReply(404, new { ok = false, message = "No pilot by that name." });
        var them = _m.Accounts.Find(with);
        return JsonReply(200, new { ok = true, with = them?.Name ?? with, messages = list.Select(m => MessageJson(m, me)), more = list.Count == 50 });
    }

    private static object MessageJson(DirectMessage m, Account me) => new
    {
        id = m.Id, from = m.From, to = m.To, text = m.Text, at = m.At,
        mine = string.Equals(m.From, me.Name, StringComparison.OrdinalIgnoreCase),
    };
}
