using System.Text;
using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class DirectoryProtocolTests
{
    private static byte[] ZoneList(int count)
    {
        var b = new List<byte> { 1 };
        for (var i = 0; i < count; i++)
        {
            b.AddRange(new byte[] { 10, 0, 0, (byte)i });
            b.AddRange(BitConverter.GetBytes((ushort)(5000 + i)));
            b.AddRange(BitConverter.GetBytes((ushort)i));
            b.AddRange(new byte[] { 1, 0, 134, 0, 0, 0 });
            var n = new byte[64];
            Encoding.ASCII.GetBytes($"Zone {i}").CopyTo(n, 0);
            b.AddRange(n);
            b.AddRange(Encoding.ASCII.GetBytes($"Description number {i}"));
            b.Add(0);
        }
        return b.ToArray();
    }

    private static byte[] Reliable(int seq, byte[] inner)
    {
        var b = new byte[6 + inner.Length];
        b[1] = 3;
        BitConverter.GetBytes(seq).CopyTo(b, 2);
        inner.CopyTo(b, 6);
        return b;
    }

    private static IEnumerable<byte[]> Sized(byte[] data, int chunk)
    {
        for (var o = 0; o < data.Length; o += chunk)
        {
            var part = data.AsSpan(o, Math.Min(chunk, data.Length - o)).ToArray();
            yield return new byte[] { 0, 0x0A }.Concat(BitConverter.GetBytes(data.Length)).Concat(part).ToArray();
        }
    }

    private static DirectoryProtocol Connected()
    {
        var p = new DirectoryProtocol();
        p.Handle(new byte[] { 0, 2, 1, 2, 3, 4 });
        return p;
    }

    [Fact]
    public void connect_request_is_vie_type_one()
    {
        var c = DirectoryProtocol.ConnectRequest(0x01020304);
        Assert.Equal(new byte[] { 0, 1, 4, 3, 2, 1, 1, 0 }, c);
    }

    [Fact]
    public void asks_once_after_connecting_as_reliable_zero()
    {
        var p = Connected();
        var o = Assert.Single(p.TakeOutgoing());
        Assert.Equal(new byte[] { 0, 3, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, o);
        p.Handle(new byte[] { 0, 2, 1, 2, 3, 4 });
        Assert.Empty(p.TakeOutgoing());
    }

    [Fact]
    public void sized_list_in_reliable_packets_is_acked_and_assembled()
    {
        var p = Connected();
        p.TakeOutgoing();
        var list = ZoneList(40);
        var seq = 0;
        foreach (var part in Sized(list, 470))
        {
            Assert.False(p.Done);
            p.Handle(Reliable(seq++, part));
        }
        Assert.True(p.Done);
        Assert.Equal(40, p.Result!.Count);
        Assert.Equal("Zone 7", p.Result[7].Name);
        Assert.Equal("10.0.0.7:5007", p.Result[7].Address);
        var acks = p.TakeOutgoing();
        Assert.Equal(seq, acks.Count);
        Assert.All(acks, a => Assert.Equal(4, a[1]));
    }

    [Fact]
    public void duplicate_reliable_is_acked_but_not_appended_twice()
    {
        var p = Connected();
        var parts = Sized(ZoneList(10), 300).ToList();
        p.Handle(Reliable(0, parts[0]));
        p.Handle(Reliable(0, parts[0])); // resent
        for (var i = 1; i < parts.Count; i++) p.Handle(Reliable(i, parts[i]));
        Assert.Equal(10, p.Result!.Count);
    }

    [Fact]
    public void reliable_packets_out_of_order_are_put_back_in_order()
    {
        var p = Connected();
        var parts = Sized(ZoneList(20), 400).ToList();
        Assert.True(parts.Count >= 4);
        var order = Enumerable.Range(0, parts.Count).Reverse().ToList(); // last first
        foreach (var i in order) p.Handle(Reliable(i, parts[i]));
        Assert.Equal(20, p.Result!.Count);
        Assert.Equal("Zone 19", p.Result[19].Name);
    }

    [Fact]
    public void reliable_packets_far_ahead_are_dropped()
    {
        var p = Connected();
        p.Handle(Reliable(5000, ZoneList(1)));
        Assert.Null(p.Result);
        p.Handle(Reliable(0, ZoneList(1)));
        Assert.Single(p.Result!);
    }

    [Fact]
    public void cluster_and_big_data_work()
    {
        var p = Connected();
        var small = ZoneList(1);
        var cluster = new List<byte> { 0, 0x0E, 2, 0, 5, (byte)small.Length };
        cluster.AddRange(small);
        p.Handle(cluster.ToArray());
        Assert.Single(p.Result!);

        var q = Connected();
        var list = ZoneList(5);
        q.Handle(new byte[] { 0, 8 }.Concat(list[..100]).ToArray());
        q.Handle(new byte[] { 0, 9 }.Concat(list[100..]).ToArray());
        Assert.Equal(5, q.Result!.Count);
    }

    [Fact]
    public void oversized_transfer_fails_without_allocating()
    {
        var p = Connected();
        p.Handle(new byte[] { 0, 0x0A }.Concat(BitConverter.GetBytes(DirectoryProtocol.MaxListBytes + 1)).Concat(new byte[10]).ToArray());
        Assert.NotNull(p.Failure);
        Assert.Null(p.Result);
    }

    [Fact]
    public void chunk_past_total_fails()
    {
        var p = Connected();
        p.Handle(new byte[] { 0, 0x0A }.Concat(BitConverter.GetBytes(4)).Concat(new byte[10]).ToArray());
        Assert.NotNull(p.Failure);
    }

    [Fact]
    public void disconnect_fails()
    {
        var p = new DirectoryProtocol();
        p.Handle(new byte[] { 0, 7 });
        Assert.True(p.Done);
        Assert.Null(p.Result);
    }
}
