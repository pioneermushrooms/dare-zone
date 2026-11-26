using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// The in-game Graphics overlay's other graphics (/account/api/graphics...): the signed-in pilot's
// own pictures for bombs, bullets, prizes and the rest (ships stay in the shipyard), and the switch
// between their own ships and graphics and the arena's. New pictures come from the same generators
// as the arena editor's, starting from the pilot's current picture or the stock one; nothing is
// saved until the pilot approves it.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private async Task<Reply> PilotGraphicsRouteAsync(HttpListenerRequest req, string api)
    {
        var method = req.HttpMethod;
        if (api == "/graphics")
            return method == "GET" ? PilotGraphicsList(req) : MethodNotAllowed();
        if (api == "/graphics/image")
            return method == "GET" ? PilotGraphicImage(req) : MethodNotAllowed();
        if (method != "POST") return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused) return refused;
        if (api == "/graphics/set") return await PilotGraphicSetAsync(req).ConfigureAwait(false);
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var id = cur.Account.Id;
        return api switch
        {
            "/graphics/clear" => ResultReply(_m.Accounts.ClearPilotGraphic(id, Str(body, "slot"))),
            "/graphics/use" => ResultReply(_m.Accounts.SetUsePilotGraphics(id, Str(body, "own") != "false")),
            "/graphics/generate" => await PilotGraphicGenerateAsync(req, id, body).ConfigureAwait(false),
            _ => Text(404, "Not found."),
        };
    }

    /// <summary>Every graphic but the ships: its label, hint, generators and whether the pilot has their own.</summary>
    private Reply PilotGraphicsList(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var (updated, useOwn) = _m.Accounts.PilotGraphics(cur.Account.Id);
        return JsonReply(200, new
        {
            ok = true,
            useOwn,
            // Ships are in the shipyard, weapons in the armory.
            slots = GameGraphics.Slots.Where(s => GameGraphics.ShipIndex(s.Name) == 0 && AccountStore.FindArmoryKind(s.Name) is null).Select(s => new
            {
                name = s.Name,
                label = s.Label,
                group = s.Group,
                hint = s.Hint,
                generate = s.Generate,
                own = updated.GetValueOrDefault(s.Name),
            }),
        });
    }

    /// <summary>?slot=: the pilot's own picture, or with &amp;stock=1 the game's own one.</summary>
    private Reply PilotGraphicImage(HttpListenerRequest req)
    {
        if (GameGraphics.Find(req.QueryString["slot"]) is not { } slot || GameGraphics.ShipIndex(slot.Name) > 0)
            return Text(404, "Not found.");
        if (req.QueryString["stock"] == "1")
            return Stock(slot.Name) is { } stock
                ? new Reply(200, ImageType(stock), stock, [("Cache-Control", "public, max-age=86400")])
                : Text(404, "Not found.");
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var own = _m.Accounts.PilotGraphic(cur.Account.Id, slot.Name);
        return own is null ? Text(404, "Not found.") : new Reply(200, ImageType(own), own, [("Cache-Control", "no-store")]);
    }

    /// <summary>POST ?slot= with the PNG/BMP bytes.</summary>
    private async Task<Reply> PilotGraphicSetAsync(HttpListenerRequest req)
    {
        if (req.ContentLength64 > GameGraphics.MaxImageBytes)
            return JsonReply(413, new { ok = false, message = "Images must be 1 MB or smaller." });
        var image = await ReadBodyAsync(req, GameGraphics.MaxImageBytes, UploadBodyTimeout).ConfigureAwait(false);
        if (image is null) return JsonReply(413, new { ok = false, message = "Images must be 1 MB or smaller." });
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        return ResultReply(_m.Accounts.SetPilotGraphic(cur.Account.Id, req.QueryString["slot"], image));
    }

    /// <summary>{"slot", "mode", "prompt"?}: a new picture from the ship service, not saved.</summary>
    private async Task<Reply> PilotGraphicGenerateAsync(HttpListenerRequest req, long accountId, JsonElement? body)
    {
        if (GameGraphics.Find(Str(body, "slot")) is not { } slot || GameGraphics.ShipIndex(slot.Name) > 0)
            return Bad("That isn't one of the game's graphics.");
        var mode = Str(body, "mode") ?? "";
        if (!slot.Generate.Contains(mode)) return Bad($"{slot.Label} can't be generated that way.");
        var prompt = (Str(body, "prompt") ?? "").Trim();
        if (mode == "prompt")
        {
            if (prompt.Length == 0) return Bad("Describe what you want.");
            if (prompt.Length > 500) return Bad("Keep the description under 501 characters.");
            if (!_m.ShipPromptPerAccount.TryTake("gfx:prompt:pilot:" + accountId)) return Bad("You've drawn a lot this hour. Try New look, or come back later.", 429);
            if (!_m.ShipPromptPerIp.TryTake("ship:prompt:ip:" + ClientIp(req))) return Bad(TooMany, 429);
        }
        else if (!_m.ShipRandomPerAccount.TryTake("gfx:random:pilot:" + accountId)) return Bad(TooMany, 429);

        // Start from the pilot's own picture when they have one (so Recolour turns theirs), else the stock one.
        var template = (Str(body, "from") == "stock" ? null : _m.Accounts.PilotGraphic(accountId, slot.Name)) ?? Stock(slot.Name);
        if (template is null) return Bad("There's no picture to start from.");
        try
        {
            using var content = JsonContent(new { kind = slot.Name, mode, prompt = mode == "prompt" ? prompt : null, template = Convert.ToBase64String(template) });
            using var res = await ShipService.PostAsync(ShipServiceUrl + "/graphic", content).ConfigureAwait(false);
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var status = (int)res.StatusCode;
            _m.Log(status == 200 ? LogLevel.Info : LogLevel.Warn, $"Pilot graphic {slot.Name} {mode} for account {accountId}: {status}.");
            return JsonBytes(status == 200 ? 200 : status is 400 or 429 or 503 ? status : 502, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _m.Log(LogLevel.Warn, $"Ship service unreachable at {ShipServiceUrl}: {ex.GetType().Name}.");
            return Bad("The generator is offline right now. Try again later.", 503);
        }
    }
}
