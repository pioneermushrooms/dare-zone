using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// A pilot's collected maps: private to them, capped, replaceable, removable.
public sealed class PilotMapsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-maps-{Guid.NewGuid():N}.db");
    private readonly ArenaStore _s;

    public PilotMapsTests() => _s = new ArenaStore(_db, () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Theory]
    [InlineData("#metalpub.lvl", "#metalpub")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\maps\\duel.LVL", "duel")]
    [InlineData("<script>.lvl", "script")]
    [InlineData(".lvl", "map")]
    public void display_names_are_plain(string file, string expected) => Assert.Equal(expected, ArenaStore.MapDisplayName(file));

    [Fact]
    public void add_open_replace_remove_and_only_your_own()
    {
        Assert.True(_s.SaveMap("Racer", null, "duel.lvl", [1, 2, 3, 4], out var id).Ok);
        Assert.Equal("duel", _s.Maps("racer").Single().Name);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, _s.MapFile("RACER", id)!.Value.Data);
        Assert.Null(_s.MapFile("Other", id));
        Assert.False(_s.SaveMap("Other", id, "x.lvl", [9, 9, 9, 9], out _).Ok);
        Assert.False(_s.RemoveMap("Other", id).Ok);

        Assert.True(_s.SaveMap("Racer", id, "duel v2", [5, 6, 7, 8], out var same).Ok);
        Assert.Equal(id, same);
        Assert.Equal(4, _s.Maps("Racer").Single().Size);
        Assert.Equal("duel v2", _s.Maps("Racer").Single().Name);

        Assert.True(_s.RemoveMap("Racer", id).Ok);
        Assert.Empty(_s.Maps("Racer"));
    }

    [Fact]
    public void at_most_25_per_pilot()
    {
        for (var i = 0; i < ArenaStore.MaxPilotMaps; i++)
            Assert.True(_s.SaveMap("Racer", null, $"m{i}.lvl", [0, 0, 0, 0], out _).Ok);
        Assert.False(_s.SaveMap("Racer", null, "one-more.lvl", [0, 0, 0, 0], out _).Ok);
        Assert.True(_s.SaveMap("Someone", null, "theirs.lvl", [0, 0, 0, 0], out _).Ok);
    }
}
