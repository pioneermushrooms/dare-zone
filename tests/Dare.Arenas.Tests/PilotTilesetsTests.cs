using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// A pilot's saved tilesets: private, capped, replaceable, renamable, duplicable, removable; and the
// file checks a tileset must pass before it is stored.
public sealed class PilotTilesetsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-tilesets-{Guid.NewGuid():N}.db");
    private readonly ArenaStore _s;

    public PilotTilesetsTests() => _s = new ArenaStore(_db, () => new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private static byte[] Bmp(int w = 304, int h = 160, int bpp = 8)
    {
        var stride = (w * bpp / 8 + 3) & ~3;
        var b = new byte[1078 + stride * Math.Abs(h)];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(b.Length).CopyTo(b, 2);
        BitConverter.GetBytes(1078).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(w).CopyTo(b, 18);
        BitConverter.GetBytes(h).CopyTo(b, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(b, 26);
        BitConverter.GetBytes((ushort)bpp).CopyTo(b, 28);
        return b;
    }

    private const string Roles = """{"format":"dare-tile-layout","version":1,"roles":{"single":{"open":""}},"packs":[]}""";

    [Fact]
    public void tileset_bmp_must_be_304_by_160_8_bit()
    {
        Assert.True(TilesetFile.TryCheckBmp(Bmp(), out _));
        Assert.True(TilesetFile.TryCheckBmp(Bmp(h: -160), out _)); // top-down
        Assert.False(TilesetFile.TryCheckBmp(Bmp(w: 320), out var e1)); Assert.Contains("304 x 160", e1);
        Assert.False(TilesetFile.TryCheckBmp(Bmp(bpp: 24), out _));
        Assert.False(TilesetFile.TryCheckBmp(Bmp()[..2000], out _)); // cut short
        Assert.False(TilesetFile.TryCheckBmp([1, 2, 3], out _));
        var real = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../js/fixtures/tactical2-tileset.bmp"));
        Assert.True(TilesetFile.TryCheckBmp(real, out _));
    }

    [Fact]
    public void role_map_is_optional_but_must_be_a_tile_layout()
    {
        Assert.True(TilesetFile.TryCheckRoles(null, out _));
        Assert.True(TilesetFile.TryCheckRoles("", out _));
        Assert.True(TilesetFile.TryCheckRoles(Roles, out _));
        Assert.False(TilesetFile.TryCheckRoles("{\"format\":\"other\"}", out _));
        Assert.False(TilesetFile.TryCheckRoles("[1,2]", out _));
        Assert.False(TilesetFile.TryCheckRoles("{nope", out _));
        Assert.False(TilesetFile.TryCheckRoles(new string(' ', 70_000) + "{}", out _));
    }

    [Fact]
    public void save_open_replace_rename_duplicate_remove_only_your_own()
    {
        var bmp = Bmp();
        Assert.True(_s.SaveTileset("Racer", null, "metal.bmp", bmp, Roles, out var id).Ok);
        var t = Assert.Single(_s.Tilesets("racer"));
        Assert.Equal("metal.bmp", t.Name);
        Assert.True(t.HasRoles);
        Assert.Equal(bmp, _s.TilesetFileOf("RACER", id)!.Value.Bmp);
        Assert.Equal(Roles, _s.TilesetFileOf("Racer", id)!.Value.Roles);
        Assert.Null(_s.TilesetFileOf("Other", id));
        Assert.False(_s.SaveTileset("Other", id, "x", bmp, null, out _).Ok);
        Assert.False(_s.RemoveTileset("Other", id).Ok);
        Assert.False(_s.RenameTileset("Other", id, "y").Ok);
        Assert.False(_s.DuplicateTileset("Other", id, out _).Ok);

        var bmp2 = Bmp(); bmp2[2000] = 7;
        Assert.True(_s.SaveTileset("Racer", id, "metal v2", bmp2, "", out var same).Ok);
        Assert.Equal(id, same);
        Assert.Equal(7, _s.TilesetFileOf("Racer", id)!.Value.Bmp[2000]);
        Assert.Null(_s.TilesetFileOf("Racer", id)!.Value.Roles);
        Assert.False(_s.Tilesets("Racer").Single().HasRoles);

        Assert.True(_s.RenameTileset("Racer", id, "steel!").Ok);
        Assert.Equal("steel", _s.Tilesets("Racer").Single().Name);

        Assert.True(_s.DuplicateTileset("Racer", id, out var copy).Ok);
        Assert.NotEqual(id, copy);
        Assert.Equal("steel copy", _s.TilesetFileOf("Racer", copy)!.Value.Name);
        Assert.Equal(2, _s.Tilesets("Racer").Count);

        Assert.True(_s.RemoveTileset("Racer", id).Ok);
        Assert.Equal(copy, _s.Tilesets("Racer").Single().Id);
    }

    [Fact]
    public void capped_per_pilot()
    {
        for (var i = 0; i < ArenaStore.MaxPilotTilesets; i++)
            Assert.True(_s.SaveTileset("Racer", null, $"t{i}", Bmp(), null, out _).Ok);
        Assert.False(_s.SaveTileset("Racer", null, "one more", Bmp(), null, out _).Ok);
        Assert.False(_s.DuplicateTileset("Racer", _s.Tilesets("Racer")[0].Id, out _).Ok);
        Assert.True(_s.SaveTileset("Other", null, "theirs", Bmp(), null, out _).Ok);
    }
}
