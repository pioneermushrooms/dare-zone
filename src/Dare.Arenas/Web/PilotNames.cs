using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Pilot names on one email (the home page's pilot menu and the account page): list, switch, add,
// delete. A deleted name first gives up everything it has in the arena system.
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
    /// Each arena or draft it owns goes to the name <paramref name="keepWith"/> gives (one of the
    /// pilot's other names) or is archived; then the account goes and the name is free. Mainloop.
    /// </summary>
    internal Result DeletePilotName(long accountId, string name, IReadOnlyDictionary<string, string> keepWith, out Account? deleted)
    {
        deleted = null;
        var pilots = Accounts.PilotsOf(accountId);
        var target = pilots.FirstOrDefault(a => string.Equals(a.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null) return new Result(false, $"'{name}' isn't one of your names.");
        if (pilots.Count < 2) return new Result(false, "That's your only name; you can't delete it.");
        // a zone staff name keeps its powers by name: freeing it would hand them to whoever claims it next
        if (StaffConf.IsGlobalStaff(ReadStaffConf(), target.Name))
            return new Result(false, $"'{target.Name}' is a zone staff name; ask a sysop to remove it from staff first.");
        var others = pilots.Where(a => a.Id != target.Id).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (arena, to) in keepWith)
        {
            if (string.IsNullOrWhiteSpace(to)) continue;
            if (!others.Contains(to)) return new Result(false, $"'{to}' isn't one of your other names.");
            keep[arena] = others.First(o => string.Equals(o, to, StringComparison.OrdinalIgnoreCase));
        }

        var released = Store.ReleasePilot(target.Name, target.Name, keep, out var archived);
        if (!released.Ok) return released;
        foreach (var arena in archived)
            SendPlayersOut(arena, $"Arena '{arena}' was archived: its owner deleted their pilot name.");
        foreach (var arena in keep.Keys)
        {
            RefreshGrantsIn(arena);
            RefreshGrantsIn(DraftRules.DraftOf(arena));
        }

        var result = Accounts.DeletePilot(accountId, target.Name, out deleted);
        if (!result.Ok)
        {
            Log(LogLevel.Error, $"Pilot name '{target.Name}' gave up its arenas but could not be deleted: {result.Message}");
            return result;
        }
        WebAuth.RevokeAllFor(target.Name);
        Log(LogLevel.Info, $"Pilot name '{target.Name}' deleted by its owner; kept {keep.Count}, archived {archived.Count}.");
        return result;
    }

    private static string? ReadStaffConf()
    {
        foreach (var path in new[] { Path.Combine("conf", "staff.conf"), "staff.conf" })
            if (File.Exists(path)) return File.ReadAllText(path);
        return null;
    }
}
