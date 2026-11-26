using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// The in-game Graphics overlay's armory (/account/api/armory...): the signed-in pilot's saved
// weapon sheets, which one each weapon wears for every ship or for one ship's shots, and new ones
// from the ship service's generators (Random: a fresh procedural look; Prompt: drawn by PixelLab
// from a description). Every generator starts from the stock sheet, so rows and frames stay where
// the game reads them. Nothing is saved until the pilot approves it.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private async Task<Reply> ArmoryRouteAsync(HttpListenerRequest req, string api)
    {
        var method = req.HttpMethod;
        if (api == "/armory")
            return method == "GET" ? ArmoryList(req) : MethodNotAllowed();
        if (api == "/armory/sheet")
            return method == "GET" ? ArmorySheet(req) : MethodNotAllowed();
        if (method != "POST") return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused) return refused;
        var (body, error) = await ReadJsonAsync(req, api == "/armory/add" ? MaxShipJsonBytes : MaxJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var id = cur.Account.Id;
        return api switch
        {
            "/armory/generate" => await GenerateWeaponAsync(req, id, body).ConfigureAwait(false),
            "/armory/add" => AddWeapon(id, body),
            "/armory/remove" => ResultReply(_m.Accounts.RemoveArmoryItem(id, Num(body, "id"))),
            "/armory/slot" => ResultReply(_m.Accounts.SetArmorySlot(id, Str(body, "kind"), (int)Num(body, "ship"), Num(body, "id"))),
            _ => Text(404, "Not found."),
        };
    }

    private Reply ArmoryList(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var (items, slots) = _m.Accounts.Armory(cur.Account.Id);
        return JsonReply(200, new
        {
            ok = true,
            kinds = AccountStore.ArmoryKinds.Select(k => new
            {
                name = k.Name,
                label = k.Label,
                hint = k.Hint,
                width = k.Width,
                height = k.Height,
                perShip = k.PerShip,
                generate = k.Prompt ? new[] { "random", "prompt" } : ["random"],
            }),
            items = items.Select(i => new { id = i.Id, kind = i.Kind, name = i.Name, source = i.Source, created = i.Created }),
            slots,
            max = AccountStore.MaxArmoryItems,
        });
    }

    private Reply ArmorySheet(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        if (!long.TryParse(req.QueryString["id"], out var itemId)) return Text(400, "Bad id.");
        var image = _m.Accounts.ArmoryImage(cur.Account.Id, itemId);
        if (image is null) return Text(404, "Not found.");
        // Saved sheets never change (a new look is a new item), so the browser may keep them.
        return new Reply(200, ImageType(image), image, [("Cache-Control", "private, max-age=31536000, immutable")]);
    }

    private Reply AddWeapon(long accountId, JsonElement? body)
    {
        byte[] image;
        try { image = Convert.FromBase64String(Str(body, "sheet") ?? ""); }
        catch (FormatException) { return Bad("That isn't a weapon sheet."); }
        var r = _m.Accounts.AddArmoryItem(accountId, Str(body, "kind"), Str(body, "name"), Str(body, "source") ?? "", image, out var itemId);
        return r.Ok ? JsonReply(200, new { ok = true, message = r.Message, id = itemId }) : Bad(r.Message);
    }

    /// <summary>{"kind", "mode": "random"|"prompt", "prompt"?}: a new sheet from the ship service, not saved.</summary>
    private async Task<Reply> GenerateWeaponAsync(HttpListenerRequest req, long accountId, JsonElement? body)
    {
        if (AccountStore.FindArmoryKind(Str(body, "kind")) is not { } kind) return Bad("That isn't one of the weapons.");
        var mode = Str(body, "mode");
        var prompt = (Str(body, "prompt") ?? "").Trim();
        string serviceMode;
        if (mode == "random")
        {
            if (!_m.ShipRandomPerAccount.TryTake("armory:random:" + accountId)) return Bad(TooMany, 429);
            serviceMode = "new";
        }
        else if (mode == "prompt" && kind.Prompt)
        {
            if (prompt.Length == 0) return Bad("Describe what you want.");
            if (prompt.Length > 500) return Bad("Keep the description under 501 characters.");
            if (!_m.ShipPromptPerAccount.TryTake("armory:prompt:" + accountId)) return Bad("You've drawn a lot this hour. Try Random, or come back later.", 429);
            if (!_m.ShipPromptPerIp.TryTake("ship:prompt:ip:" + ClientIp(req))) return Bad(TooMany, 429);
            serviceMode = "prompt";
        }
        else
        {
            return Bad(kind.Prompt ? "Choose Random or Prompt." : $"{kind.Label} can only be made with Random.");
        }

        if (Stock(kind.Name) is not { } template) return Bad("There's no picture to start from.");
        try
        {
            using var content = JsonContent(new { kind = kind.Name, mode = serviceMode, prompt = serviceMode == "prompt" ? prompt : null, template = Convert.ToBase64String(template) });
            using var res = await ShipService.PostAsync(ShipServiceUrl + "/graphic", content).ConfigureAwait(false);
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var status = (int)res.StatusCode;
            _m.Log(status == 200 ? LogLevel.Info : LogLevel.Warn, $"Armory {kind.Name} {mode} for account {accountId}: {status}.");
            return JsonBytes(status == 200 ? 200 : status is 400 or 429 or 503 ? status : 502, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _m.Log(LogLevel.Warn, $"Ship service unreachable at {ShipServiceUrl}: {ex.GetType().Name}.");
            return Bad("The generator is offline right now. Try again later.", 503);
        }
    }
}
