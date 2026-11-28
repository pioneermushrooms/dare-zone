using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class GameGraphicsTests
{
    [Fact]
    public void slots_cover_ships_and_the_major_graphics()
    {
        foreach (var n in new[] { "ship1", "ship8", "bullets", "bombs", "mines", "prizes", "shrapnel", "explode1" })
            Assert.NotNull(GameGraphics.Find(n));
        Assert.Null(GameGraphics.Find("tiles"));
        Assert.Equal(3, GameGraphics.ShipIndex("Ship3"));
        Assert.Equal(0, GameGraphics.ShipIndex("ship9"));
        Assert.Equal(0, GameGraphics.ShipIndex("ships"));
        Assert.Contains("prompt", GameGraphics.Find("bombs")!.Generate);
        Assert.Contains("new", GameGraphics.Find("rocket")!.Generate);
        // Every picture takes a description; only the colour gradient (a table) is recolour-only.
        foreach (var s in GameGraphics.Slots.Where(s => s.Name != "gradient" && GameGraphics.ShipIndex(s.Name) == 0))
            Assert.Contains("prompt", s.Generate);
        Assert.Contains("prompt", GameGraphics.Find("explode1")!.Generate);
        Assert.Equal(["recolor", "prompt"], GameGraphics.Find("over4")!.Generate);
        Assert.Equal(["recolor"], GameGraphics.Find("gradient")!.Generate);
    }

    [Fact]
    public void images_are_png_or_bmp_and_sized()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 160, 0, 0, 0, 16 };
        Assert.True(GameGraphics.TryCheckImage(png, out var ext, out var w, out var h, out _));
        Assert.Equal((".png", 160, 16), (ext, w, h));

        var bmp = new byte[54]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(20).CopyTo(bmp, 18); BitConverter.GetBytes(-50).CopyTo(bmp, 22);
        Assert.True(GameGraphics.TryCheckImage(bmp, out ext, out w, out h, out _));
        Assert.Equal((".bmp", 20, 50), (ext, w, h));

        Assert.False(GameGraphics.TryCheckImage("GIF89a......................"u8, out _, out _, out _, out _));
        BitConverter.GetBytes(5000).CopyTo(bmp, 18);
        Assert.False(GameGraphics.TryCheckImage(bmp, out _, out _, out _, out _));
    }

    [Fact]
    public void lvz_round_trips_files()
    {
        var a = Enumerable.Range(0, 3000).Select(i => (byte)(i % 7)).ToArray();
        var lvz = LvzFile.Write([("bombs.bm2", a), ("ship3.bm2", [1, 2, 3])]);
        var back = LvzFile.ReadFiles(lvz);
        Assert.Equal(["bombs.bm2", "ship3.bm2"], back.Select(f => f.Name));
        Assert.Equal(a, back[0].Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, back[1].Data);
        Assert.Throws<InvalidDataException>(() => LvzFile.ReadFiles([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Throws<InvalidDataException>(() => LvzFile.ReadFiles(lvz[..30]));
    }
}
