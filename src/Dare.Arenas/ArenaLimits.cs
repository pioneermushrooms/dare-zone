using System.Net;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// "Request more arenas": an account at its arena limit asks staff for more. Staff see it on the
// staff page (and online staff in game, and the zone's staff email when DareArenas:StaffEmail is
// set), pick a number and approve, which raises that account's limit; or decline with a reason.
// The pilot hears back in game on whichever of their names is online, and on their arena page.
public sealed partial class ArenaOwnership
{
    /// <summary>Address for "request more arenas" emails (DareArenas:StaffEmail); null when not set.</summary>
    private string? StaffEmail =>
        _configManager.GetStr(_configManager.Global, ConfigSection, "StaffEmail") is { Length: > 0 } e ? e.Trim() : null;

    /// <summary>The pilot's open ask and the last answer, for the arena page. Mainloop or store-safe.</summary>
    internal object LimitRequestData(string player)
    {
        var pending = Store.PendingLimitRequestOf(player);
        var last = Store.LastLimitAnswerOf(player);
        return new
        {
            pending = pending is null ? null : new { pending.Wanted, pending.Reason, pending.At },
            last = last is null ? null : new { last.Status, last.Granted, last.Note, last.At },
            maxLimit = ArenaStore.MaxArenaLimit,
            maxReason = ArenaStore.MaxLimitReasonLength,
        };
    }

    /// <summary>The pilot asks for up to <paramref name="wanted"/> arenas. Mainloop.</summary>
    internal Result RequestMoreArenasFor(string player, int wanted, string? reason)
    {
        var result = Store.RequestMoreArenas(player, wanted, reason, out var id);
        if (!result.Ok) return result;
        var owned = Store.OwnedCountOf(player);
        var line = $"[Dare.Arenas] {player} owns {owned} arenas (their limit) and asks for up to {wanted}"
            + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason!.Trim()}") + $". Decide on the staff page: {WebBaseUrl}/staff";
        _chat.SendModMessage(line);
        Log(LogLevel.Info, $"{player} asked for more arenas (#{id}: {owned} -> {wanted}).");
        if (StaffEmail is { } to && _email is { } email)
        {
            var names = string.Join(", ", Store.NamesOfAccount(player));
            var text = $"{player} (names on the account: {names}) owns {owned} arenas, their limit, and asks for up to {wanted}.\n\n"
                + $"Reason: {(string.IsNullOrWhiteSpace(reason) ? "(none given)" : reason!.Trim())}\n\n"
                + $"Approve or decline it on the staff page: {WebBaseUrl}/staff";
            var html = $"<p><b>{WebUtility.HtmlEncode(player)}</b> (names on the account: {WebUtility.HtmlEncode(names)}) owns {owned} arenas, their limit, and asks for up to <b>{wanted}</b>.</p>"
                + $"<p>Reason: {WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(reason) ? "(none given)" : reason!.Trim())}</p>"
                + $"<p><a href=\"{WebUtility.HtmlEncode(WebBaseUrl)}/staff\">Approve or decline it on the staff page</a></p>";
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await email.SendAsync(to, $"{player} asks for more arenas", text, html, CancellationToken.None).ConfigureAwait(false))
                        Log(LogLevel.Warn, $"The 'more arenas' email for {player} was not sent.");
                }
                catch (Exception ex) { Log(LogLevel.Warn, $"The 'more arenas' email for {player} failed: {ex.Message}"); }
            });
        }
        return result;
    }

    /// <summary>Staff answer an ask: "approve" with <paramref name="granted"/>, or "decline" with a note. Null without the power. Mainloop.</summary>
    internal Result? WebStaffLimit(string staff, long id, string action, int granted, string? note)
    {
        if (!WebStaffCan(staff, ApproveCapability)) return null;
        var req = Store.LimitRequest(id);
        if (req is null) return new Result(false, "No such request.");
        var result = action switch
        {
            "approve" => Store.ApproveLimit(id, staff, granted),
            "decline" => Store.DeclineLimit(id, staff, note),
            _ => new Result(false, "Approve or decline."),
        };
        Log(LogLevel.Info, $"(web) {staff} {action} more arenas for {req.Player}: {(result.Ok ? result.Message : "refused: " + result.Message)}");
        if (result.Ok)
            TellAccount(req.Player, action == "approve"
                ? $"Staff raised your arena limit to {granted}. Make one with ?makearena or on your arena page."
                : $"Staff declined your request for more arenas: {note?.Trim()}");
        return result;
    }

    /// <summary>The open asks, for the staff page.</summary>
    internal object LimitRequestsForStaff() => Store.LimitRequests(pendingOnly: true, 100).Select(r => new
    {
        id = r.Id, player = r.Player, names = Store.NamesOfAccount(r.Player), owned = Store.OwnedCountOf(r.Player),
        current = r.Current, wanted = r.Wanted, reason = r.Reason, at = r.At,
        arenas = Store.OwnedArenas(r.Player).Where(a => a.Status != ArenaStatus.Archived).Select(a => a.Name),
    }).ToList();

    /// <summary>A line to every online name of <paramref name="player"/>'s account. Mainloop.</summary>
    private void TellAccount(string player, string message)
    {
        foreach (var name in Store.NamesOfAccount(player))
            if (_playerData.FindPlayer(name) is { } online)
                _chat.SendMessage(online, message);
    }
}
