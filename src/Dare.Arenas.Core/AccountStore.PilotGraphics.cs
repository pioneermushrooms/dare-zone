namespace Dare.Arenas.Core;

/// <summary>
/// Each pilot name's own pictures for the game's other graphics (bombs, bullets, prizes...: every
/// <see cref="GameGraphics.Slots"/> slot except the ships, which live in the shipyard), and whether
/// the pilot sees their own ships and graphics or the arena's. Only this pilot sees them; the game
/// lays them over the arena's while the switch is on, and keeps them when it's off.
/// </summary>
public sealed partial class AccountStore
{
    /// <summary>When each of <paramref name="accountId"/>'s own graphics was saved, by slot, and whether they're shown.</summary>
    public (IReadOnlyDictionary<string, string> Updated, bool UseOwn) PilotGraphics(long accountId)
    {
        lock (_gate)
        {
            var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var c = Cmd("SELECT slot, updated FROM pilot_graphics WHERE account_id = $a ORDER BY slot", ("$a", accountId)))
            using (var r = c.ExecuteReader())
                while (r.Read()) updated[r.GetString(0)] = r.GetString(1);
            using var p = Cmd("SELECT use_own FROM pilot_graphics_prefs WHERE account_id = $a", ("$a", accountId));
            var useOwn = p.ExecuteScalar() is not long v || v != 0;
            return (updated, useOwn);
        }
    }

    /// <summary>The picture <paramref name="accountId"/> uses for <paramref name="slot"/>, or null.</summary>
    public byte[]? PilotGraphic(long accountId, string slot)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT image FROM pilot_graphics WHERE account_id = $a AND slot = $s",
                ("$a", accountId), ("$s", slot.ToLowerInvariant()));
            return c.ExecuteScalar() as byte[];
        }
    }

    /// <summary>Uses <paramref name="image"/> (a PNG or BMP) for <paramref name="slot"/>, replacing any before.</summary>
    public Result SetPilotGraphic(long accountId, string? slot, byte[] image)
    {
        if (GameGraphics.Find(slot) is not { } s || GameGraphics.ShipIndex(s.Name) > 0)
            return Fail("Ships are set from your shipyard; choose one of the other graphics.");
        if (!GameGraphics.TryCheckImage(image, out _, out _, out _, out var why)) return Fail(why);
        return Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id AND status = 'active'", ("$id", accountId)) == 0)
                return Fail("Sign in again first.");
            Run("INSERT OR REPLACE INTO pilot_graphics(account_id, slot, image, updated) VALUES($a, $s, $i, $u)",
                ("$a", accountId), ("$s", s.Name), ("$i", image), ("$u", Now()));
            return Ok($"{s.Label}: yours from now on.");
        });
    }

    /// <summary>Gives <paramref name="slot"/> back to the arena's (or the game's own) picture.</summary>
    public Result ClearPilotGraphic(long accountId, string? slot)
    {
        if (GameGraphics.Find(slot) is not { } s) return Fail("That isn't one of the game's graphics.");
        return Tx(() =>
        {
            Run("DELETE FROM pilot_graphics WHERE account_id = $a AND slot = $s", ("$a", accountId), ("$s", s.Name));
            return Ok($"{s.Label}: back to the arena's.");
        });
    }

    /// <summary>Whether <paramref name="accountId"/> sees their own ships and graphics (true) or the arena's.</summary>
    public Result SetUsePilotGraphics(long accountId, bool useOwn) => Tx(() =>
    {
        if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id AND status = 'active'", ("$id", accountId)) == 0)
            return Fail("Sign in again first.");
        Run("INSERT OR REPLACE INTO pilot_graphics_prefs(account_id, use_own) VALUES($a, $u)", ("$a", accountId), ("$u", useOwn ? 1 : 0));
        return Ok(useOwn ? "Showing your own ships and graphics." : "Showing the arena's ships and graphics.");
    });
}
