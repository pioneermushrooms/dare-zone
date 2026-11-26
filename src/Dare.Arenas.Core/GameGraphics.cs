namespace Dare.Arenas.Core;

/// <summary>
/// One of the game's graphics an arena can replace through a .lvz (the file inside is
/// <c>&lt;Name&gt;.bm2</c>). <see cref="Generate"/> lists the generator modes the ship service offers for it.
/// </summary>
public sealed record GraphicSlot(string Name, string Label, string Group, string Hint, IReadOnlyList<string> Generate);

/// <summary>The replaceable graphics (the client's OVERRIDABLE list plus ship1..ship8) and image checks.</summary>
public static class GameGraphics
{
    public const string LvzName = "arena-graphics.lvz";
    public const int MaxImageBytes = 1024 * 1024;
    public const int MaxSide = 4096;

    private static readonly string[] ShipNames = ["Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark"];
    private static readonly string[] ShipModes = ["ship", "ship-prompt"];
    private static readonly string[] SpriteModes = ["recolor", "new", "prompt"];
    private static readonly string[] BurstModes = ["recolor", "new"];
    private static readonly string[] RecolorOnly = ["recolor"];

    public static readonly IReadOnlyList<GraphicSlot> Slots =
    [
        .. ShipNames.Select((n, i) => new GraphicSlot($"ship{i + 1}", n, "Ships",
            "40 frames in 10 columns and 4 rows; frame 0 nose-up, each next one 9° clockwise.", ShipModes)),
        new("bullets", "Bullets", "Weapons", "5×5 frames, 4 to a row; a row per gun level and type.", SpriteModes),
        new("bombs", "Bombs", "Weapons", "16×16 frames, 10 to a row; a row per bomb level and type (thors last).", SpriteModes),
        new("mines", "Mines", "Weapons", "16×16 frames, 10 to a row; a row per mine level.", SpriteModes),
        new("shrapnel", "Shrapnel", "Weapons", "8×8 frames, 10 to a row.", SpriteModes),
        new("repel", "Repel", "Weapons", "The repel's expanding ring.", BurstModes),
        new("rocket", "Rocket", "Weapons", "The rocket's flame frames.", RecolorOnly),
        new("prizes", "Items (greens)", "Items", "16×16 frames, 10 to a row: the spinning prize.", SpriteModes),
        new("powerb", "Powerball", "Items", "16×16 frames: normal, phased and trail, a row each.", SpriteModes),
        new("flag", "Flag", "Items", "16×16 frames, 10 to a row: yours and the enemy's.", SpriteModes),
        new("goal", "Goal", "Items", "16×16 frames: yours and the enemy's.", RecolorOnly),
        new("explode0", "Bullet hit", "Effects", "The small explosion.", BurstModes),
        new("explode1", "Explosion", "Effects", "Bomb and ship explosion.", BurstModes),
        new("explode2", "Big explosion", "Effects", "The large ship explosion.", BurstModes),
        new("empburst", "EMP burst", "Effects", "EMP bomb explosion.", BurstModes),
        new("warp", "Warp", "Effects", "Warping in and out.", BurstModes),
        new("warppnt", "Warp point", "Effects", "16×16 frames.", SpriteModes),
        new("exhaust", "Exhaust", "Effects", "Thruster exhaust puffs.", RecolorOnly),
        new("shield", "Shield", "Effects", "The shields prize effect.", RecolorOnly),
        new("super", "Super", "Effects", "The super prize effect.", RecolorOnly),
        new("over1", "Overlay 1", "Screen", "Ship overlays (cloak, stealth and so on).", RecolorOnly),
        new("over2", "Overlay 2", "Screen", "Ship overlays.", RecolorOnly),
        new("over3", "Overlay 3", "Screen", "Ship overlays.", RecolorOnly),
        new("over4", "Overlay 4", "Screen", "Ship overlays.", RecolorOnly),
        new("over5", "Overlay 5", "Screen", "Ship overlays.", RecolorOnly),
        new("wall", "Wall", "Screen", "Wall shading.", RecolorOnly),
        new("gradient", "Gradient", "Screen", "Colour gradient.", RecolorOnly),
    ];

    public static GraphicSlot? Find(string? name) =>
        Slots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>1..8 for ship1..ship8, else 0.</summary>
    public static int ShipIndex(string name) =>
        name.Length == 5 && name.StartsWith("ship", StringComparison.OrdinalIgnoreCase) && name[4] is >= '1' and <= '8' ? name[4] - '0' : 0;

    /// <summary>
    /// A PNG or BMP of at most 1 MB and 4096 px a side. <paramref name="extension"/> is ".png" or ".bmp".
    /// </summary>
    public static bool TryCheckImage(ReadOnlySpan<byte> data, out string extension, out int width, out int height, out string error)
    {
        extension = ""; width = height = 0; error = "";
        if (data.Length == 0 || data.Length > MaxImageBytes) { error = "Images must be 1 MB or smaller."; return false; }
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            width = BigEndian(data[16..20]); height = BigEndian(data[20..24]); extension = ".png";
        }
        else if (data.Length >= 54 && data[0] == 'B' && data[1] == 'M')
        {
            width = BitConverter.ToInt32(data[18..22]); height = Math.Abs(BitConverter.ToInt32(data[22..26])); extension = ".bmp";
        }
        else { error = "Upload a PNG or BMP (.bm2) image."; return false; }
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
        {
            error = $"Images must be 1–{MaxSide} pixels a side.";
            return false;
        }
        return true;
    }

    private static int BigEndian(ReadOnlySpan<byte> b) => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
}
