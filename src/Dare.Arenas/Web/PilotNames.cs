using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Pilot names on one account (the home page's pilot menu and the account page): list, switch, add,
// delete. A deleted name's arenas stay with the account (shown under another of its names).
public sealed partial class ArenaOwnership
{
    /// <summary>What each of the signed-in pilot's names owns, for the delete prompt. Mainloop.</summary>
    internal object PilotHoldings(IReadOnlyList<Account> pilots) => pilots.Select(p =>
    {
        var (arenas, drafts) = Store.Holdings(p.Name);
        return new { name = p.Name, arenas, drafts };
    }).ToList();

    /// <summary>
    /// Deletes the name <paramref name="name"/> of the pilot signed in as <paramref name="accountId"/>.
    /// Its arenas, drafts, co-mod places and invites stay with the account: they move to the
    /// signed-in name (or, when that is the one going, the account's first other name). Saved items
    /// are the account's and stay too. Then the name's row goes and the name is free. Mainloop.
    /// </summary>
    internal Result DeletePilotName(long accountId, string name, out Account? deleted)
    {
        deleted = null;
        var pilots = Accounts.PilotsOf(accountId);
        var target = pilots.FirstOrDefault(a => string.Equals(a.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null) return new Result(false, $"'{name}' isn't one of your names.");
        if (pilots.Count < 2) return new Result(false, "That's your only name; you can't delete it.");
        // a zone staff name keeps its powers by name: freeing it would hand them to whoever claims it next
        if (StaffConf.IsGlobalStaff(ReadStaffConf(), target.Name))
            return new Result(false, $"'{target.Name}' is a zone staff name; ask a sysop to remove it from staff first.");
        var heir = pilots.FirstOrDefault(a => a.Id == accountId && a.Id != target.Id) ?? pilots.First(a => a.Id != target.Id);

        var touched = Store.HandOverName(target.Name, heir.Name, target.Name);
        var result = Accounts.DeletePilot(accountId, target.Name, out deleted);
        if (!result.Ok)
        {
            Log(LogLevel.Error, $"Pilot name '{target.Name}' handed its arenas to {heir.Name} but could not be deleted: {result.Message}");
            return result;
        }
        foreach (var arena in touched)
            RefreshGrantsIn(arena);
        WebAuth.RevokeAllFor(target.Name);
        Log(LogLevel.Info, $"Pilot name '{target.Name}' deleted by its owner; {touched.Count} arena entries now show {heir.Name}.");
        return touched.Count == 0 ? result
            : new Result(true, $"{result.Message} Its arenas stay yours; they now show {heir.Name}.");
    }

    private static string? ReadStaffConf()
    {
        foreach (var path in new[] { Path.Combine("conf", "staff.conf"), "staff.conf" })
            if (File.Exists(path)) return File.ReadAllText(path);
        return null;
    }
}
