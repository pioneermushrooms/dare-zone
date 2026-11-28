namespace Dare.Arenas.Core;

/// <summary>One weapon sheet in a pilot's armory (its image is fetched separately).</summary>
public sealed record ArmoryItem(long Id, string Kind, string Name, string Source, string Created, string Prompt = "");

/// <summary>
/// A weapon graphic the armory holds: the game's sheet of that name, its exact size (the game
/// reads frames at fixed cells, so a replacement keeps the stock layout), and whether it can be
/// set for one ship's shots (projectiles know their ship; repel and rocket effects don't).
/// </summary>
public sealed record ArmoryKind(string Name, string Label, int Width, int Height, bool PerShip, bool Prompt, string Hint);

/// <summary>
/// Each pilot name's armory: weapon sheets (bullets, bombs, mines, shrapnel, repel, rocket) they
/// generated or uploaded, and which one each weapon wears, for every ship (ship 0) or for shots
/// from one ship (1..8, over the every-ship one). Like the shipyard, but a slot is a weapon on a
/// ship. Only this pilot sees them, laid over the arena's while "Use my own" is on.
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxArmoryItems = 48;
    public const int MaxArmoryImageBytes = 256 * 1024;

    public static readonly IReadOnlyList<ArmoryKind> ArmoryKinds =
    [
        new("bullets", "Bullets", 20, 50, true, true, "5×5 frames, 4 to a row. Rows: levels 1–4, burst item, bouncing levels 1–4, burst going off."),
        new("bombs", "Bombs", 160, 208, true, true, "16×16 frames, 10 to a row. Rows: levels 1–4, EMP 1–4, bouncing 1–4, then thor."),
        new("mines", "Mines", 160, 128, true, true, "16×16 frames, 10 to a row. Rows: levels 1–4 (tumbling), then EMP 1–4."),
        new("shrapnel", "Shrapnel", 80, 48, true, true, "8×8 frames, 10 to a row. Rows: levels 1–3, then bouncing 1–3."),
        new("repel", "Repel", 480, 192, false, true, "96×96 frames: the repel's ring, growing frame by frame. Every ship's repel looks the same."),
        new("rocket", "Rocket", 312, 48, false, true, "24×24 frames: the rocket's trail, a flash, a glow, then smoke. Every ship's rocket looks the same."),
    ];

    public static ArmoryKind? FindArmoryKind(string? name) =>
        ArmoryKinds.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The weapon sheets of <paramref name="accountId"/>, oldest first, and per kind the item on ship 0 (every ship) and ships 1..8 (0 = none).</summary>
    public (IReadOnlyList<ArmoryItem> Items, IReadOnlyDictionary<string, long[]> Slots) Armory(long accountId)
    {
        lock (_gate)
        {
            var items = new List<ArmoryItem>();
            using (var c = Cmd("SELECT id, kind, name, source, created, prompt FROM armory_items WHERE person_id = $p ORDER BY id", ("$p", PersonKey(accountId))))
            using (var r = c.ExecuteReader())
                while (r.Read()) items.Add(new ArmoryItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
            var slots = ArmoryKinds.ToDictionary(k => k.Name, _ => new long[ShipSlots + 1]);
            using (var c = Cmd("SELECT kind, ship, item_id FROM armory_slots WHERE account_id = $a", ("$a", accountId)))
            using (var r = c.ExecuteReader())
                while (r.Read())
                {
                    var ship = r.GetInt64(1);
                    if (slots.TryGetValue(r.GetString(0), out var row) && ship is >= 0 and <= ShipSlots) row[ship] = r.GetInt64(2);
                }
            return (items, slots);
        }
    }

    /// <summary>The image of item <paramref name="itemId"/> when it belongs to <paramref name="accountId"/>.</summary>
    public byte[]? ArmoryImage(long accountId, long itemId)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT png FROM armory_items WHERE id = $id AND person_id = $p", ("$id", itemId), ("$p", PersonKey(accountId)));
            return c.ExecuteScalar() as byte[];
        }
    }

    /// <summary>Adds a weapon sheet to the armory; <paramref name="itemId"/> is its id. <paramref name="prompt"/> is what it was drawn from, if anything.</summary>
    public Result AddArmoryItem(long accountId, string? kind, string? name, string source, byte[] image, out long itemId, string? prompt = null)
    {
        itemId = 0;
        if (FindArmoryKind(kind) is not { } k) return Fail("That isn't one of the weapons.");
        if (!ShipSources.Contains(source)) return Fail("Unknown source.");
        if (!IsArmorySheet(k, image, out var why)) return Fail(why);
        var clean = CleanShipName(string.IsNullOrWhiteSpace(name) ? k.Label : name);
        long id = 0;
        var r = Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id AND status = 'active'", ("$id", accountId)) == 0)
                return Fail("Sign in again first.");
            if (Scalar("SELECT COUNT(*) FROM armory_items WHERE person_id = $p", ("$p", PersonKey(accountId))) >= MaxArmoryItems)
                return Fail($"Your armory is full ({MaxArmoryItems}). Remove one first.");
            Run("INSERT INTO armory_items(account_id, person_id, kind, name, source, png, created, prompt) VALUES($a, $who, $k, $n, $s, $p, $c, $pr)", ("$who", PersonKey(accountId)),
                ("$a", accountId), ("$k", k.Name), ("$n", clean), ("$s", source), ("$p", image), ("$c", Now()), ("$pr", CleanPrompt(prompt)));
            id = Scalar("SELECT last_insert_rowid()");
            return Ok("Added to your armory.");
        });
        itemId = id;
        return r;
    }

    /// <summary>Removes an item; any weapon wearing it goes back to the arena's.</summary>
    public Result RemoveArmoryItem(long accountId, long itemId) => Tx(() =>
    {
        if (Scalar("SELECT COUNT(*) FROM armory_items WHERE id = $id AND person_id = $p", ("$id", itemId), ("$p", PersonKey(accountId))) == 0)
            return Fail("That isn't in your armory.");
        Run("DELETE FROM armory_slots WHERE item_id = $id", ("$id", itemId)); // every name of the account that had it equipped
        Run("DELETE FROM armory_items WHERE id = $id", ("$id", itemId));
        return Ok("Removed from your armory.");
    });

    /// <summary>
    /// Puts item <paramref name="itemId"/> on weapon <paramref name="kind"/> for <paramref name="ship"/>
    /// (0 = every ship, 1..8 = shots from that ship), or takes it off with 0.
    /// </summary>
    public Result SetArmorySlot(long accountId, string? kind, int ship, long itemId)
    {
        if (FindArmoryKind(kind) is not { } k) return Fail("That isn't one of the weapons.");
        if (ship is < 0 or > ShipSlots) return Fail("Choose every ship, or a ship from 1 to 8.");
        if (ship > 0 && !k.PerShip) return Fail($"{k.Label} looks the same for every ship.");
        var where = ship == 0 ? "every ship" : $"ship {ship}";
        return Tx(() =>
        {
            if (itemId == 0)
            {
                Run("DELETE FROM armory_slots WHERE account_id = $a AND kind = $k AND ship = $s", ("$a", accountId), ("$k", k.Name), ("$s", ship));
                return Ok($"{k.Label} on {where}: back to the default.");
            }
            if (Scalar("SELECT COUNT(*) FROM armory_items WHERE id = $id AND person_id = $p AND kind = $k",
                    ("$id", itemId), ("$p", PersonKey(accountId)), ("$k", k.Name)) == 0)
                return Fail($"That isn't {k.Label.ToLowerInvariant()} in your armory.");
            Run("INSERT OR REPLACE INTO armory_slots(account_id, kind, ship, item_id) VALUES($a, $k, $s, $id)",
                ("$a", accountId), ("$k", k.Name), ("$s", ship), ("$id", itemId));
            return Ok($"{k.Label} on {where}.");
        });
    }

    /// <summary>
    /// Whether <paramref name="image"/> is a PNG or BMP of exactly <paramref name="kind"/>'s stock
    /// size (the game cuts frames at fixed cells), within <see cref="MaxArmoryImageBytes"/>.
    /// </summary>
    public static bool IsArmorySheet(ArmoryKind kind, ReadOnlySpan<byte> image, out string error)
    {
        if (image.Length > MaxArmoryImageBytes) { error = $"That sheet is over {MaxArmoryImageBytes / 1024} KB."; return false; }
        if (!GameGraphics.TryCheckImage(image, out _, out var w, out var h, out error)) return false;
        if (w != kind.Width || h != kind.Height)
        {
            error = $"A {kind.Label.ToLowerInvariant()} sheet is {kind.Width}×{kind.Height}, laid out like the game's own; that one is {w}×{h}.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Weapon pictures saved before the armory (one per weapon in pilot_graphics) become armory
    /// items worn on every ship, so nobody loses theirs. Runs once per row; others are left.
    /// </summary>
    private void MoveWeaponGraphicsToArmory()
    {
        var moved = new List<(long Account, string Kind, byte[] Image, string Updated)>();
        using (var c = Cmd("SELECT account_id, slot, image, updated FROM pilot_graphics"))
        using (var r = c.ExecuteReader())
            while (r.Read())
                if (FindArmoryKind(r.GetString(1)) is { } k && IsArmorySheet(k, (byte[])r.GetValue(2), out _))
                    moved.Add((r.GetInt64(0), k.Name, (byte[])r.GetValue(2), r.GetString(3)));
        foreach (var (account, kind, image, updated) in moved)
            Tx(() =>
            {
                Run("INSERT INTO armory_items(account_id, kind, name, source, png, created) VALUES($a, $k, $n, 'uploaded', $p, $c)",
                    ("$a", account), ("$k", kind), ("$n", FindArmoryKind(kind)!.Label), ("$p", image), ("$c", updated));
                var id = Scalar("SELECT last_insert_rowid()");
                Run("INSERT OR IGNORE INTO armory_slots(account_id, kind, ship, item_id) VALUES($a, $k, 0, $id)", ("$a", account), ("$k", kind), ("$id", id));
                Run("DELETE FROM pilot_graphics WHERE account_id = $a AND slot = $k", ("$a", account), ("$k", kind));
                return Ok("");
            });
    }
}
