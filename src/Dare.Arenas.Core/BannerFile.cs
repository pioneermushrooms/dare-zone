using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Dare.Arenas.Core;

/// <summary>
/// A SubSpace banner: the 12×8 picture shown beside a pilot's name, 96 bytes of indexes into the
/// game's 256-colour palette, rows bottom-up as in a BMP (what the banner packet carries). A banner
/// file is that as an 8-bit BMP; one in other colours (another palette, 24 or 32 bits) is read by
/// matching each pixel to the game palette's nearest colour, so every banner we keep is the
/// canonical 1174-byte file the game and the Banner Emporium use.
/// </summary>
public static class BannerFile
{
    public const int Width = 12;
    public const int Height = 8;
    public const int PixelBytes = Width * Height;
    private const int HeaderBytes = 14 + 40;
    public const int FileBytes = HeaderBytes + 256 * 4 + PixelBytes; // 1174
    /// <summary>The most a banner file may be: a 32-bit 12×8 BMP with room for a big header and palette.</summary>
    public const int MaxFileBytes = 8 * 1024;

    /// <summary>The game's palette as a BMP colour table (blue, green, red, 0), the same in every Emporium banner from 2004 to now.</summary>
    private static readonly byte[] GamePalette = Convert.FromBase64String(
        "AAAAADk5OQBCQkIAUlJSAFpaWgBzc3MAe3t7AISEhACMjIwAlJSUAJycnAClpaUAra2tAMbGxgDOzs4AtbW9AK2ttQCUlJwAc3N7AGNjawDe3v8AxsbvAHNzjADOzv8AtbXvAEpKYwCEhLUAtbX/AISE5wBCQnsAEBAhAHt7/wApKYQAGBhSAAgIzgAhKc4AUmP3AEpazgBaY5QAMUKlAAgx3gAIOf8AABhzAAQtTQAAKaUAWmN7AFKE/wA5Y84ACEK9AAAhYwCEtf8AGGvvABA5cwA5hN4AUlpjAISMlABKjM4AKVqEAEKl/wAYjPcApdb/AGOMrQAIUoQAEHOtAAhrpQC95/8AEHu1AAhjlACt3vcAa4ycAHO11gBChKUAKbX3ACGt7wBaa3MAMUJKAITO7wBjzv8AQq3eACm9/wBClLUAQr3vADHG/wAIQloACJTOAITe/wAhtecA3vf/AM7n7wCUrbUAc6W1AJzn/wBa1v8Ae5ylAFp7hADW9/8Are//ABiEnAApxucAIZytACHn/wClra0AjJycAEJKSgCUra0AKTExAISlpQAYWloAMc7OADn//wAhhHsAOefWADG1pQA5xq0AOYRzAEK1lAB7zrUAMZRzAClrUgBj970AQqV7AFLepQBCtXsA3vfnAHOMewCcvaUAe/ecAL3nxgBrtXsAQpRSACmEOQAQORgAWt5rACFaKQBj/3MASs5aADmUQgBjzmsAQq1KAK21rQC1zrUAOUI5ACExIQCl96UAOXM5ACFSIQApaykAOZQ5ABhSGAAxSggAOUIQAFI5MQCMOTEAznNrALVSSgD/7+8A/+fnANa9vQB7a2sA987OAPfGxgBaQkIA56WlAP+1tQBrSkoA1pSUAOecnACtc3MApWtrAP+lpQDOhIQAnGNjALVzcwCEUlIAxnt7AGtCQgCUWloAe0pKAKVjYwBjOTkASikpAHtCQgBjMTEAczk5AM5jYwD/Y2MA1lJSAL1CQgBjISEA/1JSALU5OQDeQkIAcyEhAHspMQCUWmMArYSMAIxaYwClKUoAxhBCAJQAKQCUKUoA55y1ADEYIQCcQmMADi0/AFIAIQB7ADEArWOEANZChAC1IWMAhAA5AO+EtQD3a60ArUJzAMZKhAB7KVIAcyFKAGMAMQCUAEoApQBSAFo5SgB7SmMA/5TOAMZrnACtQnsA3lKcAIQISgA5ACEAxmOtAGMASgClY5wAcwBjAHtzewAKGQoASgBKAFIAUgBzAHsAc0p7AEIxSgBCKWMAc2uEAHtzlACUc/8AQjljAHNrlABKOZwAnIz/AHNrvQBzY+8AOSnOAEo5/wAhGHsAe3P3AEI51gApIaUA////AA==");

    /// <summary>Palette entry <paramref name="i"/> as (red, green, blue).</summary>
    public static (byte R, byte G, byte B) Colour(int i) => (GamePalette[i * 4 + 2], GamePalette[i * 4 + 1], GamePalette[i * 4]);

    /// <summary>Whether <paramref name="b"/> is a BMP 12 pixels wide and 8 high (before any closer check).</summary>
    public static bool Looks(ReadOnlySpan<byte> b) =>
        b.Length >= HeaderBytes && b[0] == 'B' && b[1] == 'M'
        && BinaryPrimitives.ReadInt32LittleEndian(b[18..22]) == Width && Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b[22..26])) == Height;

    /// <summary>Null when <paramref name="b"/> is a banner the game can show, else why not.</summary>
    public static string? Check(ReadOnlySpan<byte> b) => TryPixels(b, out _, out var error) ? null : error;

    /// <summary>The canonical banner file for <paramref name="b"/> (a banner that passed <see cref="Check"/>).</summary>
    public static byte[] Normalise(byte[] b) => TryPixels(b, out var px, out var error) ? Bmp(px) : throw new ArgumentException(error, nameof(b));

    /// <summary>
    /// The 96 palette indexes of a 12×8 BMP (8-bit with any colour table, or 24/32-bit; uncompressed),
    /// rows bottom-up. Colours that aren't the game's become its nearest one.
    /// </summary>
    public static bool TryPixels(ReadOnlySpan<byte> b, out byte[] pixels, out string error)
    {
        pixels = [];
        if (!Looks(b)) { error = $"A banner is a {Width}×{Height} BMP."; return false; }
        if (b.Length > MaxFileBytes) { error = $"A banner file is at most {MaxFileBytes / 1024} KB."; return false; }
        var offset = BinaryPrimitives.ReadInt32LittleEndian(b[10..14]);
        var header = BinaryPrimitives.ReadInt32LittleEndian(b[14..18]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(b[22..26]);
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(b[28..30]);
        var compression = b.Length >= 34 ? BinaryPrimitives.ReadUInt32LittleEndian(b[30..34]) : 0;
        // BI_RGB, or BI_BITFIELDS on 32-bit files (which still hold BGRx in the usual order)
        if (compression != 0 && !(compression == 3 && bpp == 32)) { error = "Save the banner as an uncompressed BMP."; return false; }
        if (bpp is not (8 or 24 or 32)) { error = "Save the banner as an 8-bit (256 colours) or 24-bit BMP."; return false; }
        var stride = (Width * bpp / 8 + 3) & ~3;
        if (header < 12 || offset < 14 + header || offset + stride * Height > b.Length) { error = "That banner file is cut short."; return false; }
        var colours = 0;
        if (bpp == 8)
        {
            var used = b.Length >= 50 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(b[46..50]) : 0;
            colours = used is > 0 and <= 256 ? used : 256;
            if (14 + header + colours * 4 > offset) colours = (offset - 14 - header) / 4;
            if (colours <= 0) { error = "That banner has no colour table."; return false; }
        }
        var table = b.Slice(14 + header, colours * 4);
        var same = bpp == 8 && colours == 256 && table.SequenceEqual(GamePalette);
        pixels = new byte[PixelBytes];
        for (var row = 0; row < Height; row++)
        {
            // keep the BMP's bottom-up order whatever the file's own
            var src = b.Slice(offset + (rawHeight < 0 ? Height - 1 - row : row) * stride, stride);
            for (var x = 0; x < Width; x++)
            {
                byte index;
                if (bpp == 8)
                {
                    var i = src[x];
                    if (i >= colours) { error = "That banner uses a colour its table doesn't have."; pixels = []; return false; }
                    index = same ? i : Nearest(table[i * 4 + 2], table[i * 4 + 1], table[i * 4]);
                }
                else
                {
                    var p = x * (bpp / 8);
                    index = Nearest(src[p + 2], src[p + 1], src[p]);
                }
                pixels[row * Width + x] = index;
            }
        }
        error = "";
        return true;
    }

    /// <summary>The game palette's closest colour to (r, g, b); black is always 0.</summary>
    public static byte Nearest(byte r, byte g, byte b)
    {
        var best = 0;
        var bestD = int.MaxValue;
        for (var i = 0; i < 256; i++)
        {
            var (pr, pg, pb) = Colour(i);
            var d = (pr - r) * (pr - r) * 3 + (pg - g) * (pg - g) * 4 + (pb - b) * (pb - b) * 2;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return (byte)best;
    }

    /// <summary>The canonical banner file: an 8-bit BMP with the game palette.</summary>
    public static byte[] Bmp(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != PixelBytes) throw new ArgumentException($"A banner is {PixelBytes} bytes.", nameof(pixels));
        var f = new byte[FileBytes];
        f[0] = (byte)'B'; f[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(2), FileBytes);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(10), FileBytes - PixelBytes);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(18), Width);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(22), Height);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(28), 8);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(34), PixelBytes);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(46), 256);
        GamePalette.CopyTo(f, HeaderBytes);
        pixels.CopyTo(f.AsSpan(FileBytes - PixelBytes));
        return f;
    }

    /// <summary>The hash a banner is de-duplicated by: of its canonical file, so the same picture matches however it was saved.</summary>
    public static string Sha(ReadOnlySpan<byte> pixels) => Convert.ToHexString(SHA256.HashData(Bmp(pixels)));

    // ---- colour words, so banners can be found by colour ------------------------------------------

    private static readonly (string Word, int Hue)[] Hues =
        [("red", 0), ("orange", 30), ("yellow", 55), ("green", 120), ("cyan", 180), ("blue", 225), ("purple", 275), ("pink", 320), ("red", 360)];

    /// <summary>Words for the colours covering a good part of a banner (black, its usual background, aside).</summary>
    public static IReadOnlyList<string> ColourWords(ReadOnlySpan<byte> pixels)
    {
        var counts = new Dictionary<string, int>();
        var lit = 0;
        foreach (var i in pixels)
        {
            if (i == 0) continue;
            lit++;
            var w = WordOf(Colour(i));
            counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        if (lit == 0) return ["black"];
        return [.. counts.Where(c => c.Value * 5 >= lit).OrderByDescending(c => c.Value).Select(c => c.Key).Take(3)];
    }

    private static string WordOf((byte R, byte G, byte B) c)
    {
        var (r, g, b) = (c.R / 255.0, c.G / 255.0, c.B / 255.0);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var light = (max + min) / 2;
        var sat = max == min ? 0 : (max - min) / (1 - Math.Abs(2 * light - 1));
        if (sat < 0.2 || max - min < 0.08) return light > 0.8 ? "white" : light < 0.15 ? "black" : "grey";
        if (light < 0.12) return "black";
        double hue = max == r ? 60 * (((g - b) / (max - min) + 6) % 6) : max == g ? 60 * ((b - r) / (max - min) + 2) : 60 * ((r - g) / (max - min) + 4);
        if (hue is >= 15 and < 45 && light < 0.4) return "brown";
        return Hues.OrderBy(h => Math.Abs(h.Hue - hue)).First().Word;
    }

    // ---- atlas pages: many banners in one small PNG ------------------------------------------------

    /// <summary>
    /// One PNG holding <paramref name="banners"/> (96-byte pixel blocks) in a grid of <paramref name="columns"/>
    /// across, left to right then down, each 12×8 at its own cell: an 8-bit PNG in the game palette,
    /// a few KB for 200 banners. Rows of a partial last line are left black.
    /// </summary>
    public static byte[] AtlasPng(IReadOnlyList<byte[]> banners, int columns)
    {
        columns = Math.Max(1, columns);
        var rows = Math.Max(1, (banners.Count + columns - 1) / columns);
        int w = columns * Width, h = rows * Height;
        var raw = new byte[(w + 1) * h]; // a filter byte (0) at the start of each line
        for (var n = 0; n < banners.Count; n++)
        {
            var px = banners[n];
            int cx = n % columns * Width, cy = n / columns * Height;
            for (var y = 0; y < Height; y++)
                px.AsSpan((Height - 1 - y) * Width, Width).CopyTo(raw.AsSpan((cy + y) * (w + 1) + 1 + cx));
        }
        var plte = new byte[256 * 3];
        for (var i = 0; i < 256; i++) { var (r, g, b) = Colour(i); plte[i * 3] = r; plte[i * 3 + 1] = g; plte[i * 3 + 2] = b; }
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 3; // 8-bit indexed
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "PLTE", plte);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
