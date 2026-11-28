using System.Buffers.Binary;

namespace Dare.Arenas.Core;

/// <summary>
/// What a .lvl map file holds: an optional prefix (a BMP tileset, perhaps with eLVL data, or a bare
/// eLVL block) and then one u32 per tile: x (12 bits) | y (12 bits) &lt;&lt; 12 | tile (8 bits) &lt;&lt; 24,
/// on a 1024×1024 grid. The same reading as the map editor's lvl.js.
/// </summary>
public static class LvlFile
{
    public const int Side = 1024;
    /// <summary>The most a shared map may be: room for a 128 KB tileset and ~220,000 tiles. Most maps are 50–300 KB.</summary>
    public const int MaxGalleryBytes = 1024 * 1024;

    /// <summary>Where the tiles start (the prefix's length), or -1 when the prefix is cut short.</summary>
    public static int TilesStart(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 6 && b[0] == 'B' && b[1] == 'M')
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(b[2..6]);
            return size is < 54 || size > b.Length ? -1 : (int)size;
        }
        if (b.Length >= 8 && b[..4].SequenceEqual("elvl"u8))
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(b[4..8]);
            return size is < 8 || size > b.Length ? -1 : (int)size;
        }
        return 0;
    }

    /// <summary>Whether <paramref name="b"/> reads as a map: a prefix with tiles after it, or only tile entries.
    /// A file with no prefix must hold only good tiles (on the grid, not 0), so other files don't pass for one.</summary>
    public static bool Looks(ReadOnlySpan<byte> b)
    {
        var start = TilesStart(b);
        if (start < 0 || b.Length - start < 4) return false;
        if (start > 0) return true; // a prefix with tiles after it (Check says when they're cut short)
        if (b.Length % 4 != 0) return false;
        for (var p = 0; p + 4 <= b.Length; p += 4)
            if (!GoodTile(BinaryPrimitives.ReadUInt32LittleEndian(b[p..(p + 4)]))) return false;
        return true;
    }

    private static bool GoodTile(uint v) => (v & 0xfff) < Side && ((v >> 12) & 0xfff) < Side && v >> 24 != 0;

    /// <summary>Null when <paramref name="b"/> is a map with at least one tile on the grid, else why not.</summary>
    public static string? Check(ReadOnlySpan<byte> b)
    {
        var start = TilesStart(b);
        if (start < 0) return "That map file is cut short.";
        if ((b.Length - start) % 4 != 0) return "That map file is cut short (its tiles don't end evenly).";
        var good = 0;
        for (var p = start; p + 4 <= b.Length; p += 4)
            if (GoodTile(BinaryPrimitives.ReadUInt32LittleEndian(b[p..(p + 4)]))) good++;
        return good == 0 ? "That map has no tiles." : null;
    }

    /// <summary>The tiles (x, y, tile) of a map, for tests and checks.</summary>
    public static IEnumerable<(int X, int Y, int Tile)> Tiles(byte[] b)
    {
        var start = Math.Max(0, TilesStart(b));
        for (var p = start; p + 4 <= b.Length; p += 4)
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
            if (GoodTile(v)) yield return ((int)(v & 0xfff), (int)((v >> 12) & 0xfff), (int)(v >> 24));
        }
    }

    /// <summary>The map's own tileset as a stand-alone BMP (eLVL data cut off), or null when it has none.</summary>
    public static byte[]? TilesetOf(ReadOnlySpan<byte> b)
    {
        var start = TilesStart(b);
        if (start <= 0 || !b[..2].SequenceEqual("BM"u8)) return null;
        var bmp = b[..start].ToArray();
        // eLVL: the BMP header's reserved field is where the eLVL data starts, inside the prefix.
        var elvl = BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(6, 4));
        if (elvl >= 54 && elvl < bmp.Length)
        {
            bmp = bmp[..(int)elvl];
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)bmp.Length);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(6, 4), 0);
        return bmp;
    }
}

/// <summary>Maps (.lvl) and tilesets (304×160 8-bit BMP sheets, or the one inside a map) in the Foundry.</summary>
public sealed partial class AccountStore
{
    /// <summary>The side of a map's preview: the whole 1024×1024 grid at a quarter.</summary>
    public const int MapThumbSide = 256;

    private static IEnumerable<GalleryFileKind> MapGalleryKinds() =>
    [
        new(new GalleryKind("map", "Map", "Maps and tilesets", LvlFile.Side, LvlFile.Side, "maps"), LvlFile.MaxGalleryBytes,
            LvlFile.Looks, LvlFile.Check, Extension: "lvl", ThumbWidth: MapThumbSide, ThumbHeight: MapThumbSide),
        new(new GalleryKind("tileset", "Tileset", "Maps and tilesets", TilesetFile.Width, TilesetFile.Height, "tilesets"), LvlFile.MaxGalleryBytes,
            IsTilesetFile, CheckTilesetFile, TilesetBytes, Extension: "bm2", ContentType: "image/bmp"),
    ];

    /// <summary>
    /// Keeps a shared map or tileset in <paramref name="pilot"/>'s maps or tilesets in
    /// <paramref name="library"/> (the Arena Editor's, where the map editor opens and applies them).
    /// Null when the item isn't a map or tileset (it is kept by <see cref="SaveGalleryItem"/>).
    /// </summary>
    public (Result Result, string Kept, long KeptId)? KeepGalleryInLibrary(long itemId, ArenaStore library, string pilot)
    {
        if (GalleryImage(itemId) is not { } item) return null;
        var save = FindGalleryKind(item.Kind)?.Save;
        if (save is not ("maps" or "tilesets")) return null;
        long id;
        var r = save == "maps"
            ? library.SaveMap(pilot, null, item.Name, item.Image, out id)
            : library.SaveTileset(pilot, null, item.Name, item.Image, null, out id);
        if (r.Ok) CountGallerySave(itemId);
        return (r, save, id);
    }

    /// <summary>A 304×160 BMP, or a map that carries one.</summary>
    private static bool IsTilesetFile(ReadOnlySpan<byte> b)
    {
        if (b.Length < 26 || b[0] != 'B' || b[1] != 'M') return false;
        if (LvlFile.TilesStart(b) is var start and > 0 && start < b.Length) return LvlFile.Looks(b) && LvlFile.Check(b) is null && LvlFile.TilesetOf(b) is not null;
        return BinaryPrimitives.ReadInt32LittleEndian(b[18..22]) == TilesetFile.Width && Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b[22..26])) == TilesetFile.Height;
    }

    private static string? CheckTilesetFile(ReadOnlySpan<byte> b)
    {
        var bmp = TilesetBytes(b.ToArray());
        return TilesetFile.TryCheckBmp(bmp, out var error) ? null : error;
    }

    /// <summary>What a shared tileset keeps: the sheet itself, cut out of a map when it came in one.</summary>
    private static byte[] TilesetBytes(byte[] b) =>
        LvlFile.TilesStart(b) is var start and > 0 && start < b.Length ? LvlFile.TilesetOf(b) ?? b : b;
}
