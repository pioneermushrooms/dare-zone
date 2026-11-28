namespace Dare.Arenas.Core;

/// <summary>The per-name setups "Load from another name" can copy.</summary>
[Flags]
public enum LoadoutParts
{
    None = 0,
    /// <summary>Keys and macros (<c>pilot_settings</c>).</summary>
    Keys = 1,
    /// <summary>The ships in the 8 shipyard slots.</summary>
    Ships = 2,
    /// <summary>The armory items equipped per kind and ship.</summary>
    Armory = 4,
    /// <summary>The own pilot graphics and the "use my own graphics" switch.</summary>
    Graphics = 8,
    All = Keys | Ships | Armory | Graphics,
}

/// <summary>
/// Loadouts are each name's own (like playing a different game mode): keys/macros, equipped
/// ships and armory, own graphics. The libraries they draw from are the account's. A pilot can
/// load one of their names' loadouts onto another name of the same account.
/// </summary>
public sealed partial class AccountStore
{
    private static readonly (LoadoutParts Part, string Table, string Columns)[] LoadoutTables =
    [
        (LoadoutParts.Keys, "pilot_settings", "text, updated"),
        (LoadoutParts.Ships, "shipyard_slots", "slot, ship_id"),
        (LoadoutParts.Armory, "armory_slots", "kind, ship, item_id"),
        (LoadoutParts.Graphics, "pilot_graphics", "slot, image, updated"),
        (LoadoutParts.Graphics, "pilot_graphics_prefs", "use_own"),
    ];

    /// <summary>
    /// Replaces <paramref name="toName"/>'s <paramref name="parts"/> with a copy of
    /// <paramref name="fromName"/>'s. Both must be names of the account signed in as
    /// <paramref name="accountId"/>.
    /// </summary>
    public Result CopyLoadout(long accountId, string fromName, string toName, LoadoutParts parts)
    {
        var from = PilotOf(accountId, fromName);
        var to = PilotOf(accountId, toName);
        if (from is null) return Fail($"'{fromName}' isn't one of your names.");
        if (to is null) return Fail($"'{toName}' isn't one of your names.");
        if (from.Id == to.Id) return Fail("Pick another of your names to load from.");
        if ((parts & LoadoutParts.All) == 0) return Fail("Choose what to load.");
        return Tx(() =>
        {
            foreach (var (part, table, cols) in LoadoutTables)
            {
                if ((parts & part) == 0) continue;
                Run($"DELETE FROM {table} WHERE account_id = $to", ("$to", to.Id));
                Run($"INSERT INTO {table}(account_id, {cols}) SELECT $to, {cols} FROM {table} WHERE account_id = $from",
                    ("$to", to.Id), ("$from", from.Id));
            }
            return Ok($"Loaded {from.Name}'s setup onto {to.Name}.");
        });
    }
}
