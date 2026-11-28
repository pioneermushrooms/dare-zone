using System.Buffers.Binary;
using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Maps (.lvl) and tilesets (304×160 8-bit BMPs, or the one inside a map) in the Foundry: detected
// from their bytes, size-limited, shared with a preview PNG, and kept in the pilot's maps and
// tilesets (the Arena Editor's library), where the map editor opens and applies them.
public sealed class GalleryMapsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-gallery-maps-{Guid.NewGuid():N}.db");
    private readonly string _lib = Path.Combine(Path.GetTempPath(), $"dare-gallery-lib-{Guid.NewGuid():N}.db");
    private readonly AccountStore _s;
    private readonly ArenaStore _w;
    private readonly Account _dare;
    private byte _mark;

    public GalleryMapsTests()
    {
        var now = () => new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        _s = new AccountStore(_db, now);
        _w = new ArenaStore(_lib, now);
        Assert.True(_s.StartSignup("dare", "hunter22pw", "dare@example.com", out var code).Ok);
        Assert.True(_s.VerifySignup("dare@example.com", code, out var a).Ok);
        _dare = a!;
    }

    public void Dispose()
    {
        _s.Dispose();
        _w.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var db in new[] { _db, _lib })
            foreach (var f in new[] { db, db + "-wal", db + "-shm", db + "-journal" })
                try { File.Delete(f); } catch { }
    }

    /// <summary>A 304×160 8-bit tileset BMP (uncompressed), its pixels marked so each differs.</summary>
    private byte[] Tileset(int bpp = 8)
    {
        var stride = (304 * bpp / 8 + 3) & ~3;
        var b = new byte[1078 + stride * 160];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(b.Length).CopyTo(b, 2);
        BitConverter.GetBytes(1078).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(304).CopyTo(b, 18);
        BitConverter.GetBytes(160).CopyTo(b, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(b, 26);
        BitConverter.GetBytes((ushort)bpp).CopyTo(b, 28);
        b[^1] = ++_mark;
        return b;
    }

    private static byte[] Tiles(params (int X, int Y, int T)[] tiles)
    {
        var b = new byte[tiles.Length * 4];
        for (var i = 0; i < tiles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), (uint)(tiles[i].X | tiles[i].Y << 12 | tiles[i].T << 24));
        return b;
    }

    private byte[] Map(byte[]? tileset = null) => [.. tileset ?? [], .. Tiles((10, 10, 1), (11, 10, 2), (512, 512, 170), (20, ++_mark, 3))];

    private byte[] Thumb(uint side = 256)
    {
        var b = new byte[33 + 16];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), side);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), side);
        b[^1] = ++_mark;
        return b;
    }

    private static IReadOnlyList<string> Detect(byte[] file, out string error) => AccountStore.DetectGalleryKinds(file, out _, out _, out error);

    [Fact]
    public void maps_and_tilesets_are_kinds_with_their_own_keep()
    {
        Assert.Equal("maps", AccountStore.FindGalleryKind("map")!.Save);
        Assert.Equal("tilesets", AccountStore.FindGalleryKind("tileset")!.Save);
        Assert.Equal("lvl", AccountStore.FindGalleryFileKind("map")!.Extension);
        Assert.Equal("bm2", AccountStore.FindGalleryFileKind("tileset")!.Extension);
        Assert.Null(AccountStore.FindGalleryFileKind("bombs")); // stock pictures aren't file kinds
    }

    [Fact]
    public void a_map_without_a_tileset_is_a_map()
    {
        Assert.Equal(["map"], Detect(Map(), out _));
        var elvl = new byte[16];
        "elvl"u8.CopyTo(elvl);
        BinaryPrimitives.WriteUInt32LittleEndian(elvl.AsSpan(4), 16);
        Assert.Equal(["map"], Detect([.. elvl, .. Tiles((1, 1, 1))], out _));
    }

    [Fact]
    public void a_map_with_a_tileset_is_a_map_or_its_tileset_and_the_pilot_picks()
    {
        Assert.Equal(["map", "tileset"], Detect(Map(Tileset()), out _));
        // a 4-bit tileset isn't one the library takes, so that map is only a map
        Assert.Equal(["map"], Detect(Map(Tileset(4)), out _));
    }

    [Fact]
    public void a_304_by_160_sheet_is_a_tileset_and_not_any_stock_graphic()
    {
        Assert.Equal(["tileset"], Detect(Tileset(), out _));
        Assert.DoesNotContain(AccountStore.GalleryKinds, k => k.Name != "tileset" && k.Width == 304 && k.Height == 160);
        Assert.Empty(Detect(Tileset(4), out var why));
        Assert.Contains("8-bit", why);
    }

    [Fact]
    public void other_files_are_not_maps()
    {
        Assert.Empty(Detect([1, 2, 3, 4, 5, 6, 7], out _));
        Assert.Empty(Detect(Tiles((2000, 1, 1)), out _));   // off the grid
        Assert.Empty(Detect(Tiles((1, 1, 0)), out _));      // no tile
        var shortMap = Map(Tileset());
        Assert.Empty(Detect(shortMap[..^2], out var why)); // tiles cut short
        Assert.Contains("cut short", why);
        // a stock picture is still itself
        var bombs = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "gfx", "bombs.bm2"));
        Assert.Equal(["bombs"], Detect(bombs, out _));
    }

    [Fact]
    public void maps_over_1_mb_and_tilesets_over_128_kb_are_refused()
    {
        var big = new byte[LvlFile.MaxGalleryBytes + 4];
        for (var p = 0; p < big.Length; p += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(p), (uint)(p / 4 % 1024 | (p / 4 / 1024 % 1024) << 12 | 1 << 24));
        Assert.Empty(Detect(big, out var why));
        Assert.Contains("1024 KB", why);

        var fat = Tileset();
        Array.Resize(ref fat, TilesetFile.MaxBmpBytes + 1);
        BitConverter.GetBytes(fat.Length).CopyTo(fat, 2);
        Assert.Empty(Detect(fat, out why));
        Assert.Contains("128 KB", why);
    }

    [Fact]
    public void a_map_is_shared_with_its_preview_and_the_preview_is_served()
    {
        var map = Map();
        var none = _s.AddGalleryItem(_dare.Id, null, 0, "Arena 9", "rocks", null, "uploaded", map);
        Assert.False(none.Ok);
        Assert.Contains("preview", none.Message);
        var wrong = _s.AddGalleryItem(_dare.Id, null, 0, "Arena 9", "rocks", null, "uploaded", map, Thumb(128));
        Assert.False(wrong.Ok);
        Assert.Contains("256×256", wrong.Message);

        var thumb = Thumb();
        var r = _s.AddGalleryItem(_dare.Id, null, 0, "Arena 9", "rocks", null, "uploaded", map, thumb);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(thumb, _s.GalleryThumb(r.Id));
        Assert.Equal(map, _s.GalleryImage(r.Id)!.Value.Image);
        var item = Assert.Single(_s.Gallery(0, "map").Items);
        Assert.Equal("dare", item.By);
        Assert.Contains("rocks", item.Keywords);
        Assert.Contains("arena", item.Keywords);
        Assert.Single(_s.Gallery(0, null, query: "rock").Items);
        Assert.Equal(1, _s.Gallery(0, null, pilot: "dare").Count);
    }

    [Fact]
    public void a_map_with_a_tileset_waits_for_the_pilot_to_choose_and_its_tileset_alone_can_be_shared()
    {
        var ts = Tileset();
        var map = Map(ts);
        var ask = _s.AddGalleryItem(_dare.Id, null, 0, "Duel", "", null, "uploaded", map, Thumb());
        Assert.False(ask.Ok);
        Assert.Equal(["map", "tileset"], ask.Choices);

        var r = _s.AddGalleryItem(_dare.Id, "tileset", 0, "Duel tiles", "", null, "uploaded", map);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(ts, _s.GalleryImage(r.Id)!.Value.Image); // just the sheet, cut out of the map
        Assert.Null(_s.GalleryThumb(r.Id));                    // a tileset is its own picture
        Assert.True(TilesetFile.TryCheckBmp(_s.GalleryImage(r.Id)!.Value.Image, out _));

        var again = _s.AddGalleryItem(_dare.Id, "tileset", 0, "Same", "", null, "uploaded", ts);
        Assert.False(again.Ok); // the same sheet, uploaded on its own, is a duplicate
        Assert.Contains("already shared", again.Message);

        Assert.False(_s.AddGalleryItem(_dare.Id, "tileset", 0, "x", "", null, "uploaded", Tileset(), Thumb()).Ok); // no preview for tilesets
        Assert.True(_s.AddGalleryItem(_dare.Id, "map", 0, "Duel", "", null, "uploaded", map, Thumb()).Ok);
    }

    [Fact]
    public void a_tileset_inside_a_map_with_elvl_data_is_cut_to_the_sheet()
    {
        var ts = Tileset();
        var elvl = new byte[24];
        "elvl"u8.CopyTo(elvl);
        var prefix = (byte[])[.. ts, .. elvl];
        BitConverter.GetBytes(prefix.Length).CopyTo(prefix, 2); // the prefix covers the eLVL data
        BitConverter.GetBytes(ts.Length).CopyTo(prefix, 6);      // which starts where the BMP ends
        var map = Map(prefix);
        Assert.Equal(["map", "tileset"], Detect(map, out _));
        var sheet = LvlFile.TilesetOf(map)!;
        Assert.Equal(ts.Length, sheet.Length);
        Assert.Equal(ts.Length, BitConverter.ToInt32(sheet, 2));
        Assert.Equal(0, BitConverter.ToInt32(sheet, 6));
        Assert.Equal(ts[54..], sheet[54..]);
        Assert.Equal(4, LvlFile.Tiles(map).Count());
    }

    [Fact]
    public void keep_puts_a_map_in_the_pilots_maps_where_the_editor_opens_and_applies_it()
    {
        var map = Map(Tileset());
        var r = _s.AddGalleryItem(_dare.Id, "map", 0, "Duel", "", null, "uploaded", map, Thumb());
        var k = _s.KeepGalleryInLibrary(r.Id, _w, "dare")!.Value;
        Assert.True(k.Result.Ok, k.Result.Message);
        Assert.Equal("maps", k.Kept);
        var kept = Assert.Single(_w.Maps("dare"));
        Assert.Equal(k.KeptId, kept.Id);
        Assert.Equal("Duel", kept.Name);
        Assert.Equal(map, _w.MapFile("dare", kept.Id)!.Value.Data);
        // Apply publishes those bytes as the arena's map: they pass the map upload check
        Assert.True(UploadPolicy.TryCheck(UploadKind.Map, "arena.lvl", map, 0, out var why), why);
        Assert.Equal(1, Assert.Single(_s.Gallery(0, "map").Items).Saves);
        // keeping it again doesn't make a second copy
        Assert.True(_s.KeepGalleryInLibrary(r.Id, _w, "dare")!.Value.Result.Ok);
        Assert.Single(_w.Maps("dare"));
    }

    [Fact]
    public void keep_puts_a_tileset_in_the_pilots_tilesets_where_the_editor_puts_it_into_a_map()
    {
        var ts = Tileset();
        var r = _s.AddGalleryItem(_dare.Id, null, 0, "Neon", "", null, "uploaded", ts);
        Assert.True(r.Ok, r.Message);
        var k = _s.KeepGalleryInLibrary(r.Id, _w, "dare")!.Value;
        Assert.True(k.Result.Ok, k.Result.Message);
        Assert.Equal("tilesets", k.Kept);
        var kept = Assert.Single(_w.Tilesets("dare"));
        Assert.Equal("Neon", kept.Name);
        var file = _w.TilesetFileOf("dare", kept.Id)!.Value;
        Assert.Equal(ts, file.Bmp);
        Assert.True(TilesetFile.TryCheckBmp(file.Bmp, out _)); // what the editor's tileset file endpoint serves
        Assert.Empty(_w.Tilesets("someone-else"));
    }

    [Fact]
    public void keep_leaves_other_kinds_to_the_account_and_refuses_when_the_library_is_full()
    {
        var bombs = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "gfx", "bombs.bm2"));
        var b = _s.AddGalleryItem(_dare.Id, null, 0, "Bombs", "", null, "uploaded", bombs);
        Assert.True(b.Ok, b.Message);
        Assert.Null(_s.KeepGalleryInLibrary(b.Id, _w, "dare"));
        Assert.Null(_s.KeepGalleryInLibrary(999_999, _w, "dare"));

        for (var i = 0; i < ArenaStore.MaxPilotTilesets; i++) Assert.True(_w.SaveTileset("dare", null, "t" + i, Tileset(), null, out _).Ok);
        var r = _s.AddGalleryItem(_dare.Id, null, 0, "One more", "", null, "uploaded", Tileset());
        var k = _s.KeepGalleryInLibrary(r.Id, _w, "dare")!.Value;
        Assert.False(k.Result.Ok);
        Assert.Equal(0, _s.Gallery(0, "tileset").Items[0].Saves);
    }
}
