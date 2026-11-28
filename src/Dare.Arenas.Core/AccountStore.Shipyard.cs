using System.Buffers.Binary;

namespace Dare.Arenas.Core;

/// <summary>One ship in a pilot's shipyard (its sheet is fetched separately).</summary>
public sealed record ShipyardShip(long Id, string Name, string Source, string Created, string Prompt = "");

/// <summary>
/// Each pilot name's shipyard: ship rotation sheets they generated or uploaded, and which of the
/// game's eight ships each one replaces. A sheet is a PNG of 40 square frames in 10 columns and
/// 4 rows (360x144 for the stock 36-pixel ships): frame 0 at the top left is nose-up and each
/// next frame, in row order, turns 9 degrees clockwise. That is the layout the game reads
/// <c>ship1</c>..<c>ship8</c> in, so a sheet is stored and served exactly as it is drawn.
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxShipyardShips = 24;
    public const int MaxShipPngBytes = 256 * 1024;
    public const int MaxShipNameLength = 40;
    public const int ShipSlots = 8;
    public static readonly string[] ShipSources = ["generated", "random", "uploaded"];

    /// <summary>The ships of <paramref name="accountId"/>, oldest first, and the ship on each slot (1..8; 0 = the game's own).</summary>
    public (IReadOnlyList<ShipyardShip> Ships, long[] Slots) Shipyard(long accountId)
    {
        lock (_gate)
        {
            var ships = new List<ShipyardShip>();
            using (var c = Cmd("SELECT id, name, source, created, prompt FROM shipyard_ships WHERE person_id = $p ORDER BY id", ("$p", PersonKey(accountId))))
            using (var r = c.ExecuteReader())
                while (r.Read()) ships.Add(new ShipyardShip(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            var slots = new long[ShipSlots];
            using (var c = Cmd("SELECT slot, ship_id FROM shipyard_slots WHERE account_id = $a", ("$a", accountId)))
            using (var r = c.ExecuteReader())
                while (r.Read())
                {
                    var slot = r.GetInt64(0);
                    if (slot is >= 1 and <= ShipSlots) slots[slot - 1] = r.GetInt64(1);
                }
            return (ships, slots);
        }
    }

    /// <summary>The sheet of ship <paramref name="shipId"/> when it belongs to <paramref name="accountId"/>.</summary>
    public byte[]? ShipyardSheet(long accountId, long shipId)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT png FROM shipyard_ships WHERE id = $id AND person_id = $p", ("$id", shipId), ("$p", PersonKey(accountId)));
            return c.ExecuteScalar() as byte[];
        }
    }

    /// <summary>Adds a sheet to the shipyard; <paramref name="shipId"/> is its id. <paramref name="prompt"/> is what it was drawn from, if anything.</summary>
    public Result AddShipyardShip(long accountId, string? name, string source, byte[] png, out long shipId, string? prompt = null)
    {
        shipId = 0;
        if (!ShipSources.Contains(source)) return Fail("Unknown ship source.");
        if (!IsShipSheet(png, out var why)) return Fail(why);
        var clean = CleanShipName(name);
        long id = 0;
        var r = Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id AND status = 'active'", ("$id", accountId)) == 0)
                return Fail("Sign in again first.");
            if (Scalar("SELECT COUNT(*) FROM shipyard_ships WHERE person_id = $p", ("$p", PersonKey(accountId))) >= MaxShipyardShips)
                return Fail($"Your shipyard is full ({MaxShipyardShips} ships). Remove one first.");
            Run("INSERT INTO shipyard_ships(account_id, person_id, name, source, png, created, prompt) VALUES($a, $who, $n, $s, $p, $c, $pr)", ("$who", PersonKey(accountId)),
                ("$a", accountId), ("$n", clean), ("$s", source), ("$p", png), ("$c", Now()), ("$pr", CleanPrompt(prompt)));
            id = Scalar("SELECT last_insert_rowid()");
            return Ok("Added to your shipyard.");
        });
        shipId = id;
        return r;
    }

    /// <summary>Removes a ship; any slot it was on goes back to the game's own ship.</summary>
    public Result RemoveShipyardShip(long accountId, long shipId) => Tx(() =>
    {
        if (Scalar("SELECT COUNT(*) FROM shipyard_ships WHERE id = $id AND person_id = $p", ("$id", shipId), ("$p", PersonKey(accountId))) == 0)
            return Fail("That ship isn't in your shipyard.");
        Run("DELETE FROM shipyard_slots WHERE ship_id = $id", ("$id", shipId)); // every name of the account that had it in a slot
        Run("DELETE FROM shipyard_ships WHERE id = $id", ("$id", shipId));
        return Ok("Removed from your shipyard.");
    });

    /// <summary>Puts ship <paramref name="shipId"/> on <paramref name="slot"/> (1..8), or the game's own ship back with 0.</summary>
    public Result SetShipyardSlot(long accountId, int slot, long shipId)
    {
        if (slot is < 1 or > ShipSlots) return Fail("Choose a ship from 1 to 8.");
        return Tx(() =>
        {
            if (shipId == 0)
            {
                Run("DELETE FROM shipyard_slots WHERE account_id = $a AND slot = $s", ("$a", accountId), ("$s", slot));
                return Ok($"Ship {slot} is back to the default.");
            }
            if (Scalar("SELECT COUNT(*) FROM shipyard_ships WHERE id = $id AND person_id = $p", ("$id", shipId), ("$p", PersonKey(accountId))) == 0)
                return Fail("That ship isn't in your shipyard.");
            Run("INSERT OR REPLACE INTO shipyard_slots(account_id, slot, ship_id) VALUES($a, $s, $id)",
                ("$a", accountId), ("$s", slot), ("$id", shipId));
            return Ok($"Applied to ship {slot}.");
        });
    }

    /// <summary>A generation prompt as kept: printable, trimmed, at most 500 characters.</summary>
    public static string CleanPrompt(string? prompt)
    {
        var kept = new string((prompt ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return kept.Length > 500 ? kept[..500] : kept;
    }

    /// <summary>Printable, trimmed, at most <see cref="MaxShipNameLength"/> characters; "Ship" when nothing is left.</summary>
    public static string CleanShipName(string? name)
    {
        var kept = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (kept.Length > MaxShipNameLength) kept = kept[..MaxShipNameLength].TrimEnd();
        return kept.Length == 0 ? "Ship" : kept;
    }

    /// <summary>
    /// Whether <paramref name="png"/> is a PNG (by its signature and IHDR) of 40 square frames in
    /// 10 columns and 4 rows, frames 8 to 96 pixels across, within <see cref="MaxShipPngBytes"/>.
    /// The game decodes it again and skips one it can't read, so this only bounds what is kept.
    /// </summary>
    public static bool IsShipSheet(ReadOnlySpan<byte> png, out string error)
    {
        error = "";
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length > MaxShipPngBytes) { error = $"That sheet is over {MaxShipPngBytes / 1024} KB."; return false; }
        if (png.Length < 33 || !png[..8].SequenceEqual(signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            error = "That isn't a PNG ship sheet.";
            return false;
        }
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        var side = width / 10;
        if (width % 10 != 0 || height != side * 4 || side is < 8 or > 96)
        {
            error = "A ship sheet is 40 square frames in 10 columns and 4 rows (360x144 for 36-pixel ships).";
            return false;
        }
        return true;
    }
}
