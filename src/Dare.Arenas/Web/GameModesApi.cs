using System.Net;
using System.Text.Json;
using Dare.Arenas.Core;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// An account's game modes (/arena/api/gamemodes...). A game mode belongs to the account's person,
// so every pilot name on the account sees the same ones; the signed-in name is kept as made_by
// for credit. Applying one to an arena goes through /api/settings/preview and /apply (SettingSetsApi).
// Spec: docs/superpowers/specs/2026-11-27-game-mode-phase1-ships-design.md.
// ---------------------------------------------------------------------------------------------
internal sealed partial class WebServer
{
    private const int MaxGameModeJsonBytes = MaxSettingsJsonBytes + ArenaStore.MaxGameModeDataBytes;

    private async Task<Reply?> GameModesRouteAsync(HttpListenerRequest req, string path, string method)
    {
        switch (path)
        {
            case "/api/gamemodes": return method == "GET" ? GameModeList(req) : MethodNotAllowed();
            case "/api/gamemodes/get": return method == "GET" ? GameModeGet(req) : MethodNotAllowed();
            case "/api/gamemodes/save": return method == "POST" ? await GameModeSaveAsync(req).ConfigureAwait(false) : MethodNotAllowed();
            case "/api/gamemodes/rename":
            case "/api/gamemodes/duplicate":
            case "/api/gamemodes/remove":
                return method == "POST" ? await GameModeActionAsync(req, path).ConfigureAwait(false) : MethodNotAllowed();
            default: return null;
        }
    }

    /// <summary>The signed-in account's person: the only place a game mode's owner is resolved.</summary>
    private long? PersonKey(HttpListenerRequest req) =>
        CurrentAccount(req) is { } cur ? _m.Accounts.PersonOf(cur.Account.Id) : null;

    private static Reply NoPersonReply() =>
        JsonReply(401, new { ok = false, message = "Sign in with your account to keep game modes." });

    private Reply GameModeList(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out _) is { } denied) return denied;
        if (PersonKey(req) is not { } person) return NoPersonReply();
        return JsonReply(200, new { ok = true, max = ArenaStore.MaxPilotGameModes, modes = _m.WebStore.GameModes(person) });
    }

    private Reply GameModeGet(HttpListenerRequest req)
    {
        if (Gate(req, write: false, out _) is { } denied) return denied;
        if (PersonKey(req) is not { } person) return NoPersonReply();
        if (!long.TryParse(req.QueryString["id"], out var id) || _m.WebStore.GameModeOf(person, id) is not { } mode)
            return JsonReply(404, new { ok = false, message = "That game mode isn't in your collection." });
        using var data = JsonDocument.Parse(mode.Data);
        return JsonReply(200, new { ok = true, id, name = mode.Name, madeBy = mode.MadeBy, data = data.RootElement.Clone(), settings = mode.Settings });
    }

    /// <summary>POST {"id"?, "name", "data": {...}, "settings": {"Section:Key": "value"}}: a new game mode, or (with id) replaces that one.</summary>
    private async Task<Reply> GameModeSaveAsync(HttpListenerRequest req)
    {
        if (Gate(req, write: true, out var player) is { } denied) return denied;
        if (PersonKey(req) is not { } person) return NoPersonReply();
        var (body, error) = await ReadJsonAsync(req, MaxGameModeJsonBytes).ConfigureAwait(false);
        if (body is null) return error!;
        if (body.Value.ValueKind != JsonValueKind.Object || !body.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return JsonReply(400, new { ok = false, message = "Send {\"name\", \"data\": {...}, \"settings\": {...}}." });
        if (SettingsMap(body, out var bad) is not { } settings) return JsonReply(400, new { ok = false, message = bad });
        long? id = long.TryParse(Str(body, "id"), out var n) ? n : null;
        var result = _m.WebStore.SaveGameMode(person, player, id, Str(body, "name") ?? "game mode", data.GetRawText(), settings, out var savedId);
        return JsonReply(result.Ok ? 200 : 400, new { ok = result.Ok, message = result.Message, id = savedId });
    }

    /// <summary>POST {"id"} to remove or duplicate, {"id", "name"} to rename.</summary>
    private async Task<Reply> GameModeActionAsync(HttpListenerRequest req, string path)
    {
        if (Gate(req, write: true, out _) is { } denied) return denied;
        if (PersonKey(req) is not { } person) return NoPersonReply();
        var (body, error) = await ReadJsonAsync(req).ConfigureAwait(false);
        if (body is null) return error!;
        if (!long.TryParse(Str(body, "id"), out var id))
            return JsonReply(400, new { ok = false, message = "Send {\"id\"}." });
        switch (path)
        {
            case "/api/gamemodes/remove": return ResultReply(_m.WebStore.RemoveGameMode(person, id));
            case "/api/gamemodes/duplicate":
                var r = _m.WebStore.DuplicateGameMode(person, id, out var copy);
                return JsonReply(r.Ok ? 200 : 400, new { ok = r.Ok, message = r.Message, id = copy });
            default:
                if (string.IsNullOrWhiteSpace(Str(body, "name"))) return JsonReply(400, new { ok = false, message = "Send {\"id\", \"name\"}." });
                return ResultReply(_m.WebStore.RenameGameMode(person, id, Str(body, "name")!));
        }
    }
}
