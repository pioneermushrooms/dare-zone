using System.IO.Compression;
using System.Text;

namespace Dare.Arenas.Core;

/// <summary>
/// Continuum's .lvz container: "CONT", u32 section count, then per section "CONT", u32 size,
/// u32 file time, u32 compressed size, a NUL-terminated name and zlib data. Sections with an
/// empty name hold map objects; the rest are files.
/// </summary>
public static class LvzFile
{
    private const int MaxSections = 4096;
    private const int MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>The named file sections (name, bytes). Throws InvalidDataException on a damaged file.</summary>
    public static IReadOnlyList<(string Name, byte[] Data)> ReadFiles(byte[] lvz, bool namesOnly = false)
    {
        var files = new List<(string, byte[])>();
        if (lvz.Length < 8 || Encoding.ASCII.GetString(lvz, 0, 4) != "CONT") throw new InvalidDataException("not an .lvz");
        var count = BitConverter.ToInt32(lvz, 4);
        if (count < 0 || count > MaxSections) throw new InvalidDataException("bad section count");
        var p = 8;
        for (var i = 0; i < count; i++)
        {
            if (p + 16 > lvz.Length || Encoding.ASCII.GetString(lvz, p, 4) != "CONT") throw new InvalidDataException("bad section");
            var size = BitConverter.ToInt32(lvz, p + 4);
            var packed = BitConverter.ToInt32(lvz, p + 12);
            p += 16;
            var end = Array.IndexOf(lvz, (byte)0, p);
            if (end < 0) throw new InvalidDataException("bad name");
            var name = Encoding.ASCII.GetString(lvz, p, end - p);
            p = end + 1;
            if (packed < 0 || size < 0 || size > MaxFileBytes || p + packed > lvz.Length) throw new InvalidDataException("bad sizes");
            if (name.Length > 0)
                files.Add((name, namesOnly ? [] : Inflate(lvz.AsSpan(p, packed), size)));
            p += packed;
        }
        return files;
    }

    public static byte[] Write(IEnumerable<(string Name, byte[] Data)> files)
    {
        var list = files.ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("CONT"u8);
        w.Write(list.Count);
        foreach (var (name, data) in list)
        {
            var packed = Deflate(data);
            w.Write("CONT"u8);
            w.Write(data.Length);
            w.Write(0); // file time
            w.Write(packed.Length);
            w.Write(Encoding.ASCII.GetBytes(name));
            w.Write((byte)0);
            w.Write(packed);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Inflate(ReadOnlySpan<byte> packed, int size)
    {
        using var input = new MemoryStream(packed.ToArray());
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        var buf = new byte[size];
        var read = 0;
        while (read < size)
        {
            var n = z.Read(buf, read, size - read);
            if (n == 0) throw new InvalidDataException("short section");
            read += n;
        }
        return buf;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }
}
