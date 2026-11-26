using System.Buffers.Binary;
using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Each pilot name's shipyard: PNG sheets of 40 frames in 10x4, eight slots, bounded.
public sealed class ShipyardStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-shipyard-{Guid.NewGuid():N}.db");
    private readonly AccountStore _s;
    private readonly Account _dare;

    public ShipyardStoreTests()
    {
        _s = new AccountStore(_db, () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        Assert.True(_s.StartSignup("dare", "hunter22pw", "dare@example.com", out var code).Ok);
        Assert.True(_s.VerifySignup("dare@example.com", code, out var a).Ok);
        _dare = a!;
    }

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    /// <summary>A PNG signature and IHDR for a <paramref name="w"/>x<paramref name="h"/> image (the store reads no further).</summary>
    private static byte[] Png(uint w, uint h, int pad = 64)
    {
        var b = new byte[33 + pad];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    [Fact]
    public void a_sheet_is_40_square_frames_in_ten_by_four()
    {
        Assert.True(AccountStore.IsShipSheet(Png(360, 144), out _));
        Assert.True(AccountStore.IsShipSheet(Png(480, 192), out _)); // 48-pixel frames
        Assert.False(AccountStore.IsShipSheet(Png(144, 360), out _)); // turned on its side
        Assert.False(AccountStore.IsShipSheet(Png(360, 1152), out _)); // a whole ships.bm2
        Assert.False(AccountStore.IsShipSheet(Png(365, 146), out _));
        Assert.False(AccountStore.IsShipSheet(Png(40, 16), out _)); // 4-pixel frames
        Assert.False(AccountStore.IsShipSheet("BM not a png at all, long enough to be read"u8, out _));
        Assert.False(AccountStore.IsShipSheet(Png(360, 144, AccountStore.MaxShipPngBytes), out _));
    }

    [Fact]
    public void add_apply_remove()
    {
        Assert.True(_s.AddShipyardShip(_dare.Id, "  Viper\u0001 ", "generated", Png(360, 144), out var viper).Ok);
        Assert.True(_s.AddShipyardShip(_dare.Id, "", "uploaded", Png(360, 144), out var plain).Ok);
        var (ships, slots) = _s.Shipyard(_dare.Id);
        Assert.Equal(["Viper", "Ship"], ships.Select(s => s.Name));
        Assert.All(slots, s => Assert.Equal(0, s));

        Assert.True(_s.SetShipyardSlot(_dare.Id, 3, viper).Ok);
        Assert.True(_s.SetShipyardSlot(_dare.Id, 8, viper).Ok);
        Assert.True(_s.SetShipyardSlot(_dare.Id, 1, plain).Ok);
        Assert.False(_s.SetShipyardSlot(_dare.Id, 9, viper).Ok);
        Assert.False(_s.SetShipyardSlot(_dare.Id, 2, 9999).Ok);
        Assert.Equal(new long[] { plain, 0, viper, 0, 0, 0, 0, viper }, _s.Shipyard(_dare.Id).Slots);

        Assert.True(_s.SetShipyardSlot(_dare.Id, 1, 0).Ok); // back to the default
        Assert.True(_s.RemoveShipyardShip(_dare.Id, viper).Ok);
        Assert.All(_s.Shipyard(_dare.Id).Slots, s => Assert.Equal(0, s));
        Assert.Null(_s.ShipyardSheet(_dare.Id, viper));
        Assert.NotNull(_s.ShipyardSheet(_dare.Id, plain));
    }

    [Fact]
    public void ships_are_the_pilots_own_and_bounded()
    {
        Assert.True(_s.AddPilot(_dare.Id, "nerd", out var nerd).Ok);
        Assert.True(_s.AddShipyardShip(_dare.Id, "mine", "random", Png(360, 144), out var mine).Ok);
        Assert.Null(_s.ShipyardSheet(nerd!.Id, mine));
        Assert.False(_s.SetShipyardSlot(nerd.Id, 1, mine).Ok);
        Assert.False(_s.RemoveShipyardShip(nerd.Id, mine).Ok);
        Assert.False(_s.AddShipyardShip(_dare.Id, "x", "stolen", Png(360, 144), out _).Ok);

        for (var i = 1; i < AccountStore.MaxShipyardShips; i++)
            Assert.True(_s.AddShipyardShip(_dare.Id, $"s{i}", "random", Png(360, 144), out _).Ok);
        Assert.False(_s.AddShipyardShip(_dare.Id, "one too many", "random", Png(360, 144), out _).Ok);

        Assert.True(_s.AddShipyardShip(nerd.Id, "n", "random", Png(360, 144), out _).Ok);
        Assert.True(_s.DeletePilot(_dare.Id, "nerd", out _).Ok);
        Assert.Empty(_s.Shipyard(nerd.Id).Ships);
    }
}
