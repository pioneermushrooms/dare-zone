using System.Buffers.Binary;
using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Each pilot name's own pictures for the game's other graphics, and the own/arena's switch.
public sealed class PilotGraphicsStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-pilotgfx-{Guid.NewGuid():N}.db");
    private readonly AccountStore _s;
    private readonly Account _dare;
    private readonly Account _other;

    public PilotGraphicsStoreTests()
    {
        _s = new AccountStore(_db, () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        Assert.True(_s.StartSignup("dare", "hunter22pw", "dare@example.com", out var code).Ok);
        Assert.True(_s.VerifySignup("dare@example.com", code, out var a).Ok);
        _dare = a!;
        Assert.True(_s.StartSignup("other", "hunter22pw", "other@example.com", out code).Ok);
        Assert.True(_s.VerifySignup("other@example.com", code, out a).Ok);
        _other = a!;
    }

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    private static byte[] Png(uint w, uint h)
    {
        var b = new byte[64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    [Fact]
    public void set_replace_clear_per_pilot()
    {
        Assert.Empty(_s.PilotGraphics(_dare.Id).Updated);
        Assert.True(_s.SetPilotGraphic(_dare.Id, "Flag", Png(160, 32)).Ok);
        Assert.True(_s.SetPilotGraphic(_dare.Id, "flag", Png(320, 64)).Ok); // replaces
        Assert.True(_s.SetPilotGraphic(_dare.Id, "prizes", Png(160, 16)).Ok);
        Assert.Equal(["flag", "prizes"], _s.PilotGraphics(_dare.Id).Updated.Keys.Order());
        Assert.Equal(Png(320, 64), _s.PilotGraphic(_dare.Id, "FLAG"));
        Assert.Null(_s.PilotGraphic(_other.Id, "flag")); // only this pilot's

        Assert.True(_s.ClearPilotGraphic(_dare.Id, "flag").Ok);
        Assert.Null(_s.PilotGraphic(_dare.Id, "flag"));
        Assert.NotNull(_s.PilotGraphic(_dare.Id, "prizes"));
    }

    [Fact]
    public void ships_unknown_slots_and_bad_images_are_refused()
    {
        Assert.False(_s.SetPilotGraphic(_dare.Id, "ship3", Png(360, 144)).Ok); // the shipyard's
        Assert.False(_s.SetPilotGraphic(_dare.Id, "tiles", Png(304, 160)).Ok);
        Assert.False(_s.SetPilotGraphic(_dare.Id, null, Png(16, 16)).Ok);
        Assert.False(_s.SetPilotGraphic(_dare.Id, "bombs", Png(160, 208)).Ok); // the armory's
        Assert.False(_s.SetPilotGraphic(_dare.Id, "flag", "GIF89a not a png or bmp at all......"u8.ToArray()).Ok);
        Assert.False(_s.SetPilotGraphic(_dare.Id, "flag", Png(5000, 16)).Ok);
        Assert.False(_s.SetPilotGraphic(_dare.Id, "flag", new byte[GameGraphics.MaxImageBytes + 1]).Ok);
    }

    [Fact]
    public void own_graphics_show_until_the_pilot_picks_the_arenas()
    {
        Assert.True(_s.PilotGraphics(_dare.Id).UseOwn);
        Assert.True(_s.SetUsePilotGraphics(_dare.Id, false).Ok);
        Assert.False(_s.PilotGraphics(_dare.Id).UseOwn);
        Assert.True(_s.PilotGraphics(_other.Id).UseOwn);
        Assert.True(_s.SetUsePilotGraphics(_dare.Id, true).Ok);
        Assert.True(_s.PilotGraphics(_dare.Id).UseOwn);
    }
}
