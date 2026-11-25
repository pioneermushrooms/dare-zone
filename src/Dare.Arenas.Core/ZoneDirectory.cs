using System.Buffers.Binary;
using System.Text;

namespace Dare.Arenas.Core;

/// <summary>One zone as a directory server lists it.</summary>
public sealed record DirectoryZone(string Name, string Description, string Address, int Players);

/// <summary>
/// The directory servers' zone list (client side, port 4990): request <c>0x01 u32 minPlayers</c>;
/// reply <c>0x01</c> then entries to the end: ip[4] (a.b.c.d), u16 port, u16 players, u8 score
/// keeping, u8 hop count, u32 version, name[64] (NUL-padded), NUL-terminated description.
/// The web page lists every zone equally: by name, never by population or by who runs it.
/// </summary>
public static class ZoneDirectory
{
    public const byte ListType = 0x01;
    private const int EntryFixed = 78;
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 400;

    /// <summary>The 5-byte request for every zone (minimum 0 players).</summary>
    public static byte[] Request(uint minPlayers = 0)
    {
        var b = new byte[5];
        b[0] = ListType;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), minPlayers);
        return b;
    }

    /// <summary>Parses a reply; null when it is not a zone list. A short tail ends the list.</summary>
    public static List<DirectoryZone>? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data[0] != ListType) return null;
        var list = new List<DirectoryZone>();
        var p = 1;
        while (data.Length - p >= EntryFixed)
        {
            var ip = data.Slice(p, 4);
            var port = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]);
            var players = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 6)..]);
            var name = Text(data.Slice(p + 14, 64));
            p += EntryFixed;
            var end = data[p..].IndexOf((byte)0);
            var descBytes = end < 0 ? data[p..] : data.Slice(p, end);
            p += end < 0 ? descBytes.Length : end + 1;
            if (name.Length == 0) continue;
            list.Add(new DirectoryZone(name, Clean(Text(descBytes), MaxDescriptionLength),
                $"{ip[0]}.{ip[1]}.{ip[2]}.{ip[3]}:{port}", players));
        }
        return list;
    }

    /// <summary>
    /// One list from several directories: a zone listed by more than one (same address) appears
    /// once, with the highest player count reported; sorted by name (case-insensitive), then address.
    /// </summary>
    public static List<DirectoryZone> Merge(IEnumerable<IEnumerable<DirectoryZone>> lists)
    {
        var byAddress = new Dictionary<string, DirectoryZone>(StringComparer.Ordinal);
        foreach (var list in lists)
            foreach (var z in list)
                if (!byAddress.TryGetValue(z.Address, out var seen) || z.Players > seen.Players)
                    byAddress[z.Address] = seen is null ? z : z with { Name = seen.Name, Description = seen.Description.Length > 0 ? seen.Description : z.Description };
        return byAddress.Values
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(z => z.Address, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>"host:port" entries separated by spaces or commas; bad entries are skipped.</summary>
    public static List<(string Host, int Port)> ParseServers(string? text)
    {
        var list = new List<(string, int)>();
        foreach (var item in (text ?? "").Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = item.LastIndexOf(':');
            var host = colon < 0 ? item : item[..colon];
            var port = 4990;
            if (colon >= 0 && (!int.TryParse(item[(colon + 1)..], out port) || port is < 1 or > 65535)) continue;
            if (host.Length is 0 or > 253) continue;
            list.Add((host, port));
        }
        return list;
    }

    // Latin-1 (the game's 8-bit text); control characters out, whitespace collapsed.
    private static string Text(ReadOnlySpan<byte> b)
    {
        var end = b.IndexOf((byte)0);
        if (end >= 0) b = b[..end];
        return Clean(Encoding.Latin1.GetString(b), MaxNameLength * 8);
    }

    private static string Clean(string s, int max)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length >= max) break;
        }
        return sb.ToString();
    }
}
