using System.Text;
using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class ZoneDirectoryTests
{
    private static byte[] Entry(byte[] ip, ushort port, ushort players, string name, string desc)
    {
        var b = new List<byte>();
        b.AddRange(ip);
        b.AddRange(BitConverter.GetBytes(port));
        b.AddRange(BitConverter.GetBytes(players));
        b.Add(1); b.Add(0);
        b.AddRange(BitConverter.GetBytes(134u));
        var n = new byte[64];
        Encoding.Latin1.GetBytes(name).CopyTo(n, 0);
        b.AddRange(n);
        b.AddRange(Encoding.Latin1.GetBytes(desc));
        b.Add(0);
        return b.ToArray();
    }

    private static byte[] List(params byte[][] entries) => new byte[] { 1 }.Concat(entries.SelectMany(e => e)).ToArray();

    [Fact]
    public void request_is_type_and_min_players() =>
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0 }, ZoneDirectory.Request());

    [Fact]
    public void parses_entries()
    {
        var zones = ZoneDirectory.Parse(List(
            Entry([5, 161, 238, 245], 5000, 4, "Foundation", "Player-owned arenas"),
            Entry([10, 0, 0, 1], 5400, 51, "SSCU Trench Wars", "")))!;
        Assert.Equal(2, zones.Count);
        Assert.Equal(new DirectoryZone("Foundation", "Player-owned arenas", "5.161.238.245:5000", 4), zones[0]);
        Assert.Equal("10.0.0.1:5400", zones[1].Address);
        Assert.Equal(51, zones[1].Players);
    }

    [Fact]
    public void other_packets_are_not_a_list() => Assert.Null(ZoneDirectory.Parse(new byte[] { 2, 0 }));

    [Fact]
    public void short_tail_ends_the_list_and_missing_nul_is_fine()
    {
        var e = Entry([1, 2, 3, 4], 1, 1, "A", "desc");
        var data = List(e).Concat(new byte[10]).ToArray();
        Assert.Single(ZoneDirectory.Parse(data)!);
        var noNul = List(e[..^1]);
        Assert.Equal("desc", ZoneDirectory.Parse(noNul)![0].Description);
    }

    [Fact]
    public void control_characters_and_markup_text_are_kept_as_text_only()
    {
        var z = ZoneDirectory.Parse(List(Entry([1, 2, 3, 4], 1, 1, "Evil\u0007 <b>Zone</b>", "line1\r\nline2\tx")))![0];
        Assert.Equal("Evil <b>Zone</b>", z.Name); // the page inserts it as text, never HTML
        Assert.Equal("line1 line2 x", z.Description);
    }

    [Fact]
    public void nameless_entries_are_skipped() =>
        Assert.Empty(ZoneDirectory.Parse(List(Entry([1, 2, 3, 4], 1, 1, "", "x")))!);

    [Fact]
    public void merge_dedupes_by_address_and_sorts_by_name()
    {
        var a = new[] { new DirectoryZone("zeta", "", "1.1.1.1:1", 3), new DirectoryZone("Alpha", "d", "2.2.2.2:2", 1) };
        var b = new[] { new DirectoryZone("zeta", "z", "1.1.1.1:1", 9), new DirectoryZone("beta", "", "3.3.3.3:3", 100) };
        var m = ZoneDirectory.Merge([a, b]);
        Assert.Equal(new[] { "Alpha", "beta", "zeta" }, m.Select(z => z.Name));
        Assert.Equal(9, m[2].Players);
        Assert.Equal("z", m[2].Description);
    }

    [Fact]
    public void parses_server_list()
    {
        var s = ZoneDirectory.ParseServers("sscentral.trenchwars.org:4990, directory.poid.be  bad:port x:70000");
        Assert.Equal(new[] { ("sscentral.trenchwars.org", 4990), ("directory.poid.be", 4990) }, s);
    }
}
