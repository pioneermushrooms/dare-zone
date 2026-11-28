using System.Buffers.Binary;
using System.IO.Compression;
using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Banners: a 12×8 picture in the game palette. Read from any 12×8 BMP into the game's own 8-bit
// file, shared and kept like other designs (kept in the Armory, whose banner slot is the pilot's
// banner), and brought in by the thousand from the Banner Emporium into a library of their own:
// idempotent imports, de-duplicated pictures, and pages served as one atlas PNG each.
public sealed class BannerTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-banners-{Guid.NewGuid():N}.db");
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"dare-banner-staging-{Guid.NewGuid():N}.sqlite");
    private readonly AccountStore _s;
    private readonly Account _dare;

    public BannerTests()
    {
        _s = new AccountStore(_db, () => new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        Assert.True(_s.StartSignup("dare", "hunter22pw", "dare@example.com", out var code).Ok);
        Assert.True(_s.VerifySignup("dare@example.com", code, out var a).Ok);
        _dare = a!;
    }

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _staging, _staging + "-wal", _staging + "-shm" })
            try { File.Delete(f); } catch { }
    }

    /// <summary>96 pixels, different for each <paramref name="seed"/>.</summary>
    private static byte[] Pixels(int seed)
    {
        var px = new byte[BannerFile.PixelBytes];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)((seed * 31 + i * 7) % 256);
        px[0] = (byte)(seed & 0xFF); px[1] = (byte)(seed >> 8);
        return px;
    }

    /// <summary>A 24-bit 12×8 BMP, rows bottom-up, every pixel (r, g, b).</summary>
    private static byte[] Bmp24(Func<int, int, (byte R, byte G, byte B)> colour, bool topDown = false)
    {
        const int stride = 36; // 12 × 3, already a multiple of 4
        var b = new byte[54 + stride * 8];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), b.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), 12);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), topDown ? -8 : 8);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 24);
        for (var row = 0; row < 8; row++)
            for (var x = 0; x < 12; x++)
            {
                var y = topDown ? row : 7 - row; // y counts from the top of the picture
                var (r, g, bl) = colour(x, y);
                var p = 54 + row * stride + x * 3;
                b[p] = bl; b[p + 1] = g; b[p + 2] = r;
            }
        return b;
    }

    // ---- reading and validating banner files ------------------------------------------------------

    [Fact]
    public void The_canonical_file_is_the_games_own_and_reads_back_the_same()
    {
        var px = Pixels(5);
        var file = BannerFile.Bmp(px);
        Assert.Equal(1174, file.Length);
        Assert.True(BannerFile.Looks(file));
        Assert.True(BannerFile.TryPixels(file, out var back, out _));
        Assert.Equal(px, back);
        Assert.Equal(file, BannerFile.Normalise(file));
    }

    [Fact]
    public void Other_colours_become_the_palettes_nearest_and_top_down_files_are_turned_the_right_way_up()
    {
        // white on the top row, black elsewhere
        var white = Bmp24((x, y) => y == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));
        var whiteTopDown = Bmp24((x, y) => y == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0), topDown: true);
        Assert.True(BannerFile.TryPixels(white, out var a, out _));
        Assert.True(BannerFile.TryPixels(whiteTopDown, out var b, out _));
        Assert.Equal(a, b);
        // BMP order: the top row of the picture is the last 12 bytes
        Assert.All(a[^12..], i => Assert.Equal(BannerFile.Nearest(255, 255, 255), i));
        Assert.All(a[..84], i => Assert.Equal(0, i));
        Assert.Equal((255, 255, 255), ((int, int, int))BannerFile.Colour(BannerFile.Nearest(255, 255, 255)));
        // a colour a little off the palette lands on a palette colour close to it
        var near = BannerFile.Colour(BannerFile.Nearest(250, 10, 12));
        Assert.True(near.R > 150 && near.G < 80 && near.B < 80);
    }

    [Fact]
    public void Files_that_arent_banners_are_refused_with_a_reason()
    {
        var wide = BannerFile.Bmp(Pixels(1));
        BinaryPrimitives.WriteInt32LittleEndian(wide.AsSpan(18), 13);
        Assert.False(BannerFile.Looks(wide));
        Assert.Contains("12×8", BannerFile.Check(wide));

        var rle = BannerFile.Bmp(Pixels(1));
        BinaryPrimitives.WriteUInt32LittleEndian(rle.AsSpan(30), 1);
        Assert.Contains("uncompressed", BannerFile.Check(rle));

        var fourBit = BannerFile.Bmp(Pixels(1));
        BinaryPrimitives.WriteUInt16LittleEndian(fourBit.AsSpan(28), 4);
        Assert.Contains("8-bit", BannerFile.Check(fourBit));

        Assert.Contains("cut short", BannerFile.Check(BannerFile.Bmp(Pixels(1))[..1100]));
        Assert.Null(BannerFile.Check(Bmp24((_, _) => (1, 2, 3))));
    }

    [Fact]
    public void A_banner_is_detected_as_the_banner_kind_whatever_bmp_it_came_as()
    {
        Assert.Equal(["banner"], AccountStore.DetectGalleryKinds(BannerFile.Bmp(Pixels(2)), out _, out _, out _));
        Assert.Equal(["banner"], AccountStore.DetectGalleryKinds(Bmp24((_, _) => (200, 30, 30)), out _, out _, out _));
        var k = AccountStore.FindGalleryFileKind("banner")!;
        Assert.Equal(("bmp", "image/bmp", "armory"), (k.Extension, k.ContentType, k.Kind.Save));
        Assert.Contains(AccountStore.GalleryKinds, g => g.Name == "banner");
        // and a banner isn't a weapon the ship service makes
        var armory = AccountStore.FindArmoryKind("banner")!;
        Assert.False(armory.Generate);
        Assert.False(armory.PerShip);
    }

    [Fact]
    public void Colour_words_name_what_a_banner_mostly_is()
    {
        var red = BannerFile.TryPixels(Bmp24((_, _) => (230, 20, 20)), out var px, out _) ? px : null;
        Assert.Contains("red", BannerFile.ColourWords(red!));
        Assert.Equal(["black"], BannerFile.ColourWords(new byte[96]));
    }

    // ---- sharing, keeping, using, downloading ------------------------------------------------------

    [Fact]
    public void A_shared_banner_is_stored_as_the_games_file_kept_in_the_armory_and_can_be_made_the_pilots_banner()
    {
        var upload = Bmp24((x, _) => x < 6 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)255));
        var shared = _s.AddGalleryItem(_dare.Id, null, 0, "Half and half", "flag", null, "uploaded", upload);
        Assert.True(shared.Ok, shared.Message);
        var item = _s.GalleryImage(shared.Id)!.Value;
        Assert.Equal("banner", item.Kind);
        Assert.Equal(1174, item.Image.Length); // Download gives the raw game BMP
        Assert.True(BannerFile.TryPixels(item.Image, out var px, out _));
        Assert.Contains(_s.Gallery(0, "banner").Items, i => i.Id == shared.Id);
        Assert.Contains(_s.Gallery(0, pilot: "dare").Items, i => i.Id == shared.Id && i.By == "dare"); // credit on the profile feed

        Assert.True(_s.SaveGalleryItem(_dare.Id, shared.Id).Ok); // Keep: into the Armory
        Assert.Single(_s.Armory(_dare.Id).Items, i => i.Kind == "banner");
        Assert.Null(_s.PilotBanner(_dare.Id)); // kept, not yet worn

        var used = _s.UseGalleryBanner(_dare.Id, shared.Id);
        Assert.True(used.Ok, used.Message);
        Assert.Equal(px, _s.PilotBanner(_dare.Id));
        Assert.Single(_s.Armory(_dare.Id).Items, i => i.Kind == "banner"); // the same picture isn't kept twice

        Assert.True(_s.SetArmorySlot(_dare.Id, "banner", 0, 0).Ok); // no banner
        Assert.Null(_s.PilotBanner(_dare.Id));
        Assert.False(_s.SetArmorySlot(_dare.Id, "banner", 3, _s.Armory(_dare.Id).Items[0].Id).Ok); // one banner, not one per ship
    }

    [Fact]
    public void The_armory_takes_a_banner_in_any_bmp_and_keeps_the_games_file()
    {
        Assert.True(_s.AddArmoryItem(_dare.Id, "banner", "Mine", "uploaded", Bmp24((_, _) => (0, 200, 0)), out var id).Ok);
        var stored = _s.ArmoryImage(_dare.Id, id)!;
        Assert.Equal(1174, stored.Length);
        Assert.False(_s.AddArmoryItem(_dare.Id, "banner", "Too big", "uploaded", BannerFile.Bmp(Pixels(1))[..900], out _).Ok);
    }

    // ---- the library ---------------------------------------------------------------------------------

    private static BannerImportRow Row(long id, string author, int seed, long players = 0) =>
        new(id, author, $"2026-01-{1 + id % 28:00}T00:00", players, BannerFile.Bmp(Pixels(seed)));

    [Fact]
    public void Importing_twice_changes_nothing_and_the_same_picture_is_kept_once()
    {
        var rows = new[] { Row(1, "Kuukunen", 1), Row(2, "RENZI", 2), Row(3, "Easton", 3) };
        Assert.Equal(new BannerImportCounts(3, 0, 0, 0), _s.ImportBanners(AccountStore.EmporiumSource, rows));
        Assert.Equal(new BannerImportCounts(0, 3, 0, 0), _s.ImportBanners(AccountStore.EmporiumSource, rows));
        // the same picture again under another id, and a file that isn't a banner
        var more = new[] { Row(4, "copycat", 2), new BannerImportRow(5, "x", "", 0, [1, 2, 3]) };
        Assert.Equal(new BannerImportCounts(0, 0, 1, 1), _s.ImportBanners(AccountStore.EmporiumSource, more));
        Assert.Equal(3, _s.BannerLibrary().Count);
        // a re-run refreshes the details
        _s.ImportBanners(AccountStore.EmporiumSource, [Row(2, "RENZI", 2, players: 1186)]);
        Assert.Equal(1186, _s.BannerLibrary(sort: "popular").Items[0].Players);
        // the library is outside the gallery's caps: none of it is a gallery item
        Assert.Equal(0, _s.Gallery(0).Count);
    }

    [Fact]
    public void A_picture_a_pilot_already_shared_isnt_imported_again()
    {
        var px = Pixels(9);
        Assert.True(_s.AddGalleryItem(_dare.Id, null, 0, "Mine first", null, null, "uploaded", BannerFile.Bmp(px)).Ok);
        Assert.Equal(1, _s.ImportBanners(AccountStore.EmporiumSource, [Row(9, "someone", 9)]).Duplicates);
    }

    [Fact]
    public void A_staff_takedown_stays_down_through_another_import()
    {
        _s.ImportBanners(AccountStore.EmporiumSource, [Row(1, "a", 1), Row(2, "b", 2)]);
        var rev = _s.BannerLibraryRevision();
        var id = _s.BannerLibrary().Items.Single(b => b.Author == "a").Id;
        Assert.True(_s.HideLibraryBanner(id).Ok);
        Assert.NotEqual(rev, _s.BannerLibraryRevision());
        _s.ImportBanners(AccountStore.EmporiumSource, [Row(1, "a", 1), Row(2, "b", 2)]);
        Assert.Equal(["b"], _s.BannerLibrary().Items.Select(b => b.Author));
        Assert.Null(_s.LibraryBannerFile(id));
        Assert.False(_s.KeepLibraryBanner(_dare.Id, id, false).Ok);
    }

    [Fact]
    public void A_library_banner_is_kept_in_the_armory_downloaded_as_its_file_and_worn_as_a_banner()
    {
        _s.ImportBanners(AccountStore.EmporiumSource, [Row(26066, "Hercules", 7)]);
        var b = _s.BannerLibrary().Items.Single();
        var file = _s.LibraryBannerFile(b.Id)!.Value;
        Assert.Equal(BannerFile.Bmp(Pixels(7)), file.File);
        Assert.Equal(26066, file.Banner.SourceId);

        Assert.True(_s.KeepLibraryBanner(_dare.Id, b.Id, use: false).Ok);
        Assert.Equal("Banner by Hercules", _s.Armory(_dare.Id).Items.Single(i => i.Kind == "banner").Name);
        Assert.True(_s.KeepLibraryBanner(_dare.Id, b.Id, use: true).Ok);
        Assert.Equal(Pixels(7), _s.PilotBanner(_dare.Id));
        Assert.Single(_s.Armory(_dare.Id).Items, i => i.Kind == "banner");
        Assert.Equal(2, _s.BannerLibrary().Items.Single().Saves);
    }

    [Fact]
    public void Pages_hold_two_hundred_in_atlas_order_and_the_atlas_png_draws_them_there()
    {
        _s.ImportBanners(AccountStore.EmporiumSource, Enumerable.Range(1, 450).Select(i => Row(i, i % 2 == 0 ? "Even Steven" : "Odd Todd", i)));
        var first = _s.BannerLibrary();
        Assert.Equal((450L, 200), (first.Count, first.Items.Count));
        Assert.Equal(450, first.Items[0].SourceId); // newest first
        var last = _s.BannerLibrary(page: 2);
        Assert.Equal(50, last.Items.Count);
        Assert.Equal(1, last.Items[^1].SourceId);
        Assert.Empty(_s.BannerLibrary(page: 3).Items);
        Assert.Equal(1, _s.BannerLibrary(sort: "old").Items[0].SourceId);

        var (w, h, raw) = DecodeAtlas(_s.BannerLibraryAtlas());
        Assert.Equal((240, 80), (w, h)); // 20 across, 10 down
        // cell 0 is banner 450: its top row (the BMP's last 12 bytes) is the atlas's first line
        Assert.Equal(Pixels(450)[^12..], raw.AsSpan(1, 12).ToArray());
        // cell 21 (row 1, column 1) is the 22nd banner: 429
        Assert.Equal(Pixels(429)[^12..], raw.AsSpan(8 * (w + 1) + 1 + 12, 12).ToArray());
        var (_, lastH, _) = DecodeAtlas(_s.BannerLibraryAtlas(page: 2));
        Assert.Equal(24, lastH); // 50 banners: 3 rows

        // keyword search over authors (any word's start), and the exact author
        Assert.Equal(225, _s.BannerLibrary("odd").Count);
        Assert.Equal(225, _s.BannerLibrary("stev").Count);
        Assert.Equal(225, _s.BannerLibrary(author: "even steven").Count);
        Assert.Equal(0, _s.BannerLibrary("nobody").Count);
    }

    private static (int W, int H, byte[] Raw) DecodeAtlas(byte[] png)
    {
        Assert.Equal(0x89, png[0]);
        var w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        var h = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        Assert.Equal(3, png[25]); // indexed colour
        var idat = new MemoryStream();
        for (var p = 8; p < png.Length;)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            var type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IDAT") idat.Write(png, p + 8, len);
            p += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal((w + 1) * h, raw.Length);
        return (w, h, raw.ToArray());
    }

    // ---- the import script's staging file ------------------------------------------------------------

    private void Stage(params (long Id, string Status, string Author, byte[]? Bmp)[] rows)
    {
        using var c = new SqliteConnection($"Data Source={_staging};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        // the schema deploy/import-banner-emporium.py writes
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS banners (seq INTEGER PRIMARY KEY AUTOINCREMENT, id INTEGER NOT NULL UNIQUE, status TEXT NOT NULL, " +
                          "author TEXT, added TEXT, players INTEGER, bmp BLOB, fetched TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
        foreach (var (id, status, author, bmp) in rows)
        {
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT OR REPLACE INTO banners(id, status, author, added, players, bmp, fetched) VALUES($i, $s, $a, '2005-07-10T13:06', 3, $b, 'now')";
            ins.Parameters.AddWithValue("$i", id);
            ins.Parameters.AddWithValue("$s", status);
            ins.Parameters.AddWithValue("$a", author);
            ins.Parameters.AddWithValue("$b", (object?)bmp ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
    }

    [Fact]
    public void The_staging_file_is_brought_in_a_bit_at_a_time_and_never_twice()
    {
        Assert.Equal(new BannerImportCounts(0, 0, 0, 0), _s.IngestBannerStaging(_staging)); // nothing fetched yet
        Stage((1, "ok", "Hercules", BannerFile.Bmp(Pixels(1))), (2, "missing", "", null), (3, "ok", "Arikel", BannerFile.Bmp(Pixels(3))));
        Assert.Equal(new BannerImportCounts(2, 0, 0, 0), _s.IngestBannerStaging(_staging));
        Assert.Equal(new BannerImportCounts(0, 0, 0, 0), _s.IngestBannerStaging(_staging)); // nothing new since
        // the script carries on (and fetches one it missed before again)
        Stage((4, "ok", "Kaotix", BannerFile.Bmp(Pixels(4))), (2, "ok", "Late", BannerFile.Bmp(Pixels(2))));
        Assert.Equal(new BannerImportCounts(2, 0, 0, 0), _s.IngestBannerStaging(_staging));
        Assert.Equal(4, _s.BannerLibrary().Count);
        Assert.Equal("2005-07-10T13:06", _s.BannerLibrary().Items[0].Added);
    }
}
