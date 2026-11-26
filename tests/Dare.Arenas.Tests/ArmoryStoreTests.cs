using System.Buffers.Binary;
using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Each pilot name's armory: weapon sheets at the stock size, worn by every ship or one ship's shots.
public sealed class ArmoryStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-armory-{Guid.NewGuid():N}.db");
    private AccountStore _s;
    private readonly Account _dare;
    private readonly Account _other;

    public ArmoryStoreTests()
    {
        _s = Open();
        Assert.True(_s.StartSignup("dare", "hunter22pw", "dare@example.com", out var code).Ok);
        Assert.True(_s.VerifySignup("dare@example.com", code, out var a).Ok);
        _dare = a!;
        Assert.True(_s.StartSignup("other", "hunter22pw", "other@example.com", out code).Ok);
        Assert.True(_s.VerifySignup("other@example.com", code, out a).Ok);
        _other = a!;
    }

    private AccountStore Open() => new(_db, () => new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    private static byte[] Png(uint w, uint h, byte mark = 0)
    {
        var b = new byte[33 + 64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), h);
        b[^1] = mark;
        return b;
    }

    [Fact]
    public void add_apply_per_ship_and_remove()
    {
        Assert.True(_s.AddArmoryItem(_dare.Id, "Bombs", "Plasma", "generated", Png(160, 208, 1), out var plasma).Ok);
        Assert.True(_s.AddArmoryItem(_dare.Id, "bombs", "", "random", Png(160, 208, 2), out var spare).Ok);
        Assert.True(_s.AddArmoryItem(_dare.Id, "repel", "Ring", "uploaded", Png(480, 192), out var ring).Ok);
        var (items, slots) = _s.Armory(_dare.Id);
        Assert.Equal(["Plasma", "Bombs", "Ring"], items.Select(i => i.Name));
        Assert.Equal("bombs", items[0].Kind);
        Assert.All(slots.Values, row => Assert.Equal(9, row.Length));

        Assert.True(_s.SetArmorySlot(_dare.Id, "bombs", 0, plasma).Ok); // every ship
        Assert.True(_s.SetArmorySlot(_dare.Id, "bombs", 3, spare).Ok); // the Spider's shots
        Assert.True(_s.SetArmorySlot(_dare.Id, "repel", 0, ring).Ok);
        Assert.False(_s.SetArmorySlot(_dare.Id, "repel", 2, ring).Ok); // repel isn't per ship
        Assert.False(_s.SetArmorySlot(_dare.Id, "mines", 0, plasma).Ok); // a bomb sheet on mines
        Assert.False(_s.SetArmorySlot(_dare.Id, "bombs", 9, plasma).Ok);
        Assert.False(_s.SetArmorySlot(_other.Id, "bombs", 0, plasma).Ok); // not theirs
        slots = _s.Armory(_dare.Id).Slots;
        Assert.Equal([plasma, 0, 0, spare, 0, 0, 0, 0, 0], slots["bombs"]);
        Assert.Equal(ring, slots["repel"][0]);
        Assert.Equal(Png(160, 208, 2), _s.ArmoryImage(_dare.Id, spare));
        Assert.Null(_s.ArmoryImage(_other.Id, spare));

        Assert.True(_s.SetArmorySlot(_dare.Id, "bombs", 3, 0).Ok);
        Assert.Equal(0, _s.Armory(_dare.Id).Slots["bombs"][3]);
        Assert.True(_s.RemoveArmoryItem(_dare.Id, plasma).Ok);
        Assert.Equal(0, _s.Armory(_dare.Id).Slots["bombs"][0]); // taken off with it
        Assert.False(_s.RemoveArmoryItem(_other.Id, ring).Ok);
        Assert.Empty(_s.Armory(_other.Id).Items);
    }

    [Fact]
    public void sheets_keep_the_stock_layout()
    {
        Assert.False(_s.AddArmoryItem(_dare.Id, "bombs", "x", "uploaded", Png(320, 416), out _).Ok);
        Assert.False(_s.AddArmoryItem(_dare.Id, "bullets", "x", "uploaded", Png(160, 208), out _).Ok);
        Assert.False(_s.AddArmoryItem(_dare.Id, "prizes", "x", "uploaded", Png(160, 16), out _).Ok); // not a weapon
        Assert.False(_s.AddArmoryItem(_dare.Id, "bombs", "x", "stolen", Png(160, 208), out _).Ok);
        Assert.False(_s.AddArmoryItem(_dare.Id, "bombs", "x", "uploaded", new byte[AccountStore.MaxArmoryImageBytes + 1], out _).Ok);
        foreach (var k in AccountStore.ArmoryKinds)
            Assert.True(_s.AddArmoryItem(_dare.Id, k.Name, k.Label, "uploaded", Png((uint)k.Width, (uint)k.Height), out _).Ok, k.Name);
    }

    [Fact]
    public void the_armory_is_bounded()
    {
        for (var i = 0; i < AccountStore.MaxArmoryItems; i++)
            Assert.True(_s.AddArmoryItem(_dare.Id, "shrapnel", $"s{i}", "random", Png(80, 48), out _).Ok);
        Assert.False(_s.AddArmoryItem(_dare.Id, "shrapnel", "one more", "random", Png(80, 48), out _).Ok);
        Assert.True(_s.AddArmoryItem(_other.Id, "shrapnel", "theirs", "random", Png(80, 48), out _).Ok);
    }

    [Fact]
    public void weapon_pictures_saved_before_the_armory_move_into_it()
    {
        Assert.True(_s.SetPilotGraphic(_dare.Id, "prizes", Png(160, 16)).Ok);
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        // As an older server would have saved them.
        using (var c = new SqliteConnection($"Data Source={_db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO pilot_graphics(account_id, slot, image, updated) VALUES($a, 'bombs', $p, 'then'), ($a, 'mines', $bad, 'then')";
            cmd.Parameters.AddWithValue("$a", _dare.Id);
            cmd.Parameters.AddWithValue("$p", Png(160, 208, 7));
            cmd.Parameters.AddWithValue("$bad", Png(32, 32));
            cmd.ExecuteNonQuery();
        }
        _s = Open();
        var (items, slots) = _s.Armory(_dare.Id);
        var bombs = Assert.Single(items);
        Assert.Equal(("bombs", "Bombs"), (bombs.Kind, bombs.Name));
        Assert.Equal(bombs.Id, slots["bombs"][0]);
        Assert.Equal(Png(160, 208, 7), _s.ArmoryImage(_dare.Id, bombs.Id));
        Assert.Null(_s.PilotGraphic(_dare.Id, "bombs"));
        Assert.NotNull(_s.PilotGraphic(_dare.Id, "prizes")); // not a weapon: left alone
        Assert.NotNull(_s.PilotGraphic(_dare.Id, "mines")); // the wrong size: left alone, never lost
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        _s = Open();
        Assert.Single(_s.Armory(_dare.Id).Items); // once
    }
}
