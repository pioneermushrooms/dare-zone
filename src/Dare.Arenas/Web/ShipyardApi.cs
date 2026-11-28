using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

public sealed partial class ArenaOwnership
{
    // Generated ships, per pilot name. Prompted ones are paid (PixelLab), so they are scarcer;
    // the ship service also draws only one prompted ship at a time for the whole zone.
    // A prompt draws a batch of options (about $0.12); Try again takes the next one from it for free.
    internal readonly RateLimiter ShipPromptPerAccount = new(10, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter ShipRandomPerAccount = new(120, TimeSpan.FromHours(1), Clock);
    internal readonly RateLimiter ShipPromptPerIp = new(24, TimeSpan.FromHours(1), Clock);
}

// ---------------------------------------------------------------------------------------------
// The in-game Graphics overlay's shipyard (/account/api/shipyard...): the signed-in pilot's saved
// ship sheets, which of the eight ships each replaces, and new ones from the ship service.
// The ship service (ships/service.py) listens on loopback only and holds the PixelLab key; the
// browser never talks to it, and nothing it is sent identifies the pilot.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private const int MaxShipJsonBytes = 400 * 1024; // a base64 sheet is at most ~350 KB

    // PixelLab's queue takes 1.5-4.5 minutes for a fresh batch (the ship service gives up at 7).
    private static readonly HttpClient ShipService = new() { Timeout = TimeSpan.FromSeconds(450) };

    /// <summary>The ship service's base URL: DARE_SHIPS_URL, else the loopback default.</summary>
    private static readonly string ShipServiceUrl =
        (Environment.GetEnvironmentVariable("DARE_SHIPS_URL") is { Length: > 0 } u ? u : "http://127.0.0.1:8089").TrimEnd('/');

    private async Task<Reply> ShipyardRouteAsync(HttpListenerRequest req, string api)
    {
        var method = req.HttpMethod;
        if (api == "/shipyard")
            return method == "GET" ? ShipyardList(req) : MethodNotAllowed();
        if (api == "/shipyard/sheet")
            return method == "GET" ? ShipyardSheet(req) : MethodNotAllowed();
        if (method != "POST") return MethodNotAllowed();
        if (AccountPostCheck(req) is { } refused) return refused;
        var (body, error) = await ReadJsonAsync(req, api == "/shipyard/add" ? MaxShipJsonBytes : MaxJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var id = cur.Account.Id;
        return api switch
        {
            "/shipyard/generate" => await GenerateShipAsync(req, id, body).ConfigureAwait(false),
            "/shipyard/add" => AddShip(id, body),
            "/shipyard/remove" => ResultReply(_m.Accounts.RemoveShipyardShip(id, Num(body, "id"))),
            "/shipyard/slot" => ResultReply(_m.Accounts.SetShipyardSlot(id, (int)Num(body, "slot"), Num(body, "id"))),
            _ => Text(404, "Not found."),
        };
    }

    private Reply ShipyardList(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        var (ships, slots) = _m.Accounts.Shipyard(cur.Account.Id);
        return JsonReply(200, new
        {
            ok = true,
            name = cur.Account.Name,
            ships = ships.Select(s => new { id = s.Id, name = s.Name, source = s.Source, created = s.Created, prompt = s.Prompt }),
            slots,
            max = AccountStore.MaxShipyardShips,
        });
    }

    private Reply ShipyardSheet(HttpListenerRequest req)
    {
        if (CurrentAccount(req) is not { } cur) return NotSignedInReply();
        if (!long.TryParse(req.QueryString["id"], out var shipId)) return Text(400, "Bad ship id.");
        var png = _m.Accounts.ShipyardSheet(cur.Account.Id, shipId);
        if (png is null) return Text(404, "Not found.");
        // A ship's sheet never changes once saved (a flip or an edit is a new ship), so the
        // browser may keep it; private, because it is this pilot's.
        return new Reply(200, "image/png", png, [("Cache-Control", "private, max-age=31536000, immutable")]);
    }

    private Reply AddShip(long accountId, JsonElement? body)
    {
        byte[] png;
        try { png = Convert.FromBase64String(Str(body, "sheet") ?? ""); }
        catch (FormatException) { return Bad("That isn't a ship sheet."); }
        var r = _m.Accounts.AddShipyardShip(accountId, Str(body, "name"), Str(body, "source") ?? "", png, out var shipId, Str(body, "prompt"));
        return r.Ok ? JsonReply(200, new { ok = true, message = r.Message, id = shipId }) : Bad(r.Message);
    }

    /// <summary>Asks the ship service for one ship. Nothing is saved: the pilot approves it first, then adds it.</summary>
    private async Task<Reply> GenerateShipAsync(HttpListenerRequest req, long accountId, JsonElement? body)
    {
        var mode = Str(body, "mode");
        object request;
        if (mode == "random")
        {
            if (!_m.ShipRandomPerAccount.TryTake("ship:random:" + accountId)) return Bad(TooMany, 429);
            // Try again continues the pilot's run, so the next ship differs from those shown.
            request = Str(body, "batch") is { Length: > 0 and <= 64 } again
                ? new { mode = "random", batch = again }
                : (object)new { mode = "random" };
        }
        else if (mode == "prompt" && Str(body, "batch") is { Length: > 0 and <= 64 } batch)
        {
            // Try again on a prompt: usually a ship already drawn; the ship service caps how many
            // fresh (paid) batches one run of tries may draw.
            if (!_m.ShipRandomPerAccount.TryTake("ship:random:" + accountId)) return Bad(TooMany, 429);
            request = new { mode = "prompt", batch };
        }
        else if (mode == "prompt")
        {
            var prompt = (Str(body, "prompt") ?? "").Trim();
            if (prompt.Length == 0) return Bad("Describe the ship you want.");
            if (prompt.Length > 500) return Bad("Keep the description under 501 characters.");
            if (!_m.ShipPromptPerAccount.TryTake("ship:prompt:" + accountId)) return Bad("You've drawn a lot of ships this hour. Try Random, or come back later.", 429);
            if (!_m.ShipPromptPerIp.TryTake("ship:prompt:ip:" + ClientIp(req))) return Bad(TooMany, 429);
            request = new { mode = "prompt", prompt, creative = Str(body, "creative") == "true", variation = Str(body, "variation") == "true" };
        }
        else
        {
            return Bad("Choose Random or Prompt.");
        }

        try
        {
            using var content = JsonContent(request);
            using var res = await ShipService.PostAsync(ShipServiceUrl + "/generate", content).ConfigureAwait(false);
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            // The service's answers are written for pilots (and carry no secrets), so pass them on.
            var status = (int)res.StatusCode is >= 200 and < 600 ? (int)res.StatusCode : 502;
            _m.Log(status == 200 ? LogLevel.Info : LogLevel.Warn, $"Ship {mode} for account {accountId}: {status}.");
            return JsonBytes(status == 200 ? 200 : status is 400 or 410 or 429 or 503 ? status : 502, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _m.Log(LogLevel.Warn, $"Ship service unreachable at {ShipServiceUrl}: {ex.GetType().Name}.");
            return Bad("The ship generator is offline right now. Try again later.", 503);
        }
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload, Json), System.Text.Encoding.UTF8, "application/json");

    private static long Num(JsonElement? body, string name) =>
        long.TryParse(Str(body, name), out var n) ? n : -1;
}
