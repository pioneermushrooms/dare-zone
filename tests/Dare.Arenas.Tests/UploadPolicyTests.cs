using System.Text;
using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class UploadPolicyTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Valid_bmp_lvl_passes()
    {
        Assert.True(UploadPolicy.TryCheck(UploadKind.Map, "a.lvl", Ascii("BMxxxxxx"), 0, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void Valid_elvl_lvl_passes() =>
        Assert.True(UploadPolicy.TryCheck(UploadKind.Map, "a.LVL", Ascii("elvl123"), 0, out _));

    [Fact]
    public void Tile_only_lvl_passes() =>
        Assert.True(UploadPolicy.TryCheck(UploadKind.Map, "a.lvl", new byte[8], 0, out _));

    [Fact]
    public void Valid_lvz_passes() =>
        Assert.True(UploadPolicy.TryCheck(UploadKind.Lvz, "a.lvz", Ascii("CONTxx"), 0, out _));

    [Fact]
    public void Oversize_lvl_rejected()
    {
        var big = new byte[2 * 1024 * 1024 + 4];
        big[0] = (byte)'B'; big[1] = (byte)'M';
        Assert.False(UploadPolicy.TryCheck(UploadKind.Map, "a.lvl", big, 0, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Lvl_at_exactly_2mb_passes()
    {
        var b = new byte[2 * 1024 * 1024];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        Assert.True(UploadPolicy.TryCheck(UploadKind.Map, "a.lvl", b, 0, out _));
    }

    [Fact]
    public void Oversize_lvz_rejected()
    {
        var big = new byte[5 * 1024 * 1024 + 1];
        Array.Copy(Ascii("CONT"), big, 4);
        Assert.False(UploadPolicy.TryCheck(UploadKind.Lvz, "a.lvz", big, 0, out _));
    }

    [Fact]
    public void Wrong_extension_rejected()
    {
        Assert.False(UploadPolicy.TryCheck(UploadKind.Map, "a.txt", Ascii("BMxx"), 0, out _));
        Assert.False(UploadPolicy.TryCheck(UploadKind.Lvz, "a.lvl", Ascii("CONT"), 0, out _));
    }

    [Fact]
    public void Rejects_wrong_magic()
    {
        Assert.False(UploadPolicy.TryCheck(UploadKind.Lvz, "a.lvz", Ascii("NOPE"), 0, out _));
        Assert.False(UploadPolicy.TryCheck(UploadKind.Map, "a.lvl", Ascii("junk!"), 0, out _));
    }

    [Fact]
    public void Empty_lvl_rejected() =>
        Assert.False(UploadPolicy.TryCheck(UploadKind.Map, "a.lvl", ReadOnlySpan<byte>.Empty, 0, out _));

    [Fact]
    public void Eleventh_lvz_refused()
    {
        Assert.True(UploadPolicy.TryCheck(UploadKind.Lvz, "a.lvz", Ascii("CONT"), 9, out _));
        Assert.False(UploadPolicy.TryCheck(UploadKind.Lvz, "a.lvz", Ascii("CONT"), 10, out _));
    }

    [Fact]
    public void File_name_with_directory_rejected()
    {
        Assert.False(UploadPolicy.TryCheck(UploadKind.Map, "x/a.lvl", Ascii("BMxx"), 0, out _));
        Assert.False(UploadPolicy.TryCheck(UploadKind.Map, "x\\a.lvl", Ascii("BMxx"), 0, out _));
    }
}
