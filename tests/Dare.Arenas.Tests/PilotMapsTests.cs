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
            Assert.True(_s.SaveMap("Racer", null, $"m{i}.lvl", [0, 0, 0, (byte)i], out _).Ok);
        Assert.False(_s.SaveMap("Racer", null, "one-more.lvl", [1, 1, 1, 1], out _).Ok);
        // a file already in the full collection is still found, not refused
        Assert.True(_s.SaveMap("Racer", null, "again.lvl", [0, 0, 0, 3], out var again).Ok);
        Assert.Equal("m3", _s.Maps("Racer").Single(m => m.Id == again).Name);
        Assert.True(_s.SaveMap("Someone", null, "theirs.lvl", [0, 0, 0, 0], out _).Ok);
    }

    [Fact]
    public void the_same_file_twice_is_kept_once_and_moves_to_the_top()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        using var s = new ArenaStore(_db + "2", () => now);
        Assert.True(s.SaveMap("Racer", null, "duel.lvl", [1, 2, 3, 4], out var first).Ok);
        now = now.AddMinutes(1);
        Assert.True(s.SaveMap("Racer", null, "other.lvl", [9, 9, 9, 9], out _).Ok);
        now = now.AddMinutes(1);
        var again = s.SaveMap("racer", null, "duel copy.lvl", [1, 2, 3, 4], out var second);
        Assert.True(again.Ok);
        Assert.Equal(first, second);
        Assert.Contains("already", again.Message);
        Assert.Equal(["duel", "other"], s.Maps("Racer").Select(m => m.Name));
        // someone else's identical file is theirs, not a duplicate of Racer's
        Assert.True(s.SaveMap("Someone", null, "duel.lvl", [1, 2, 3, 4], out var theirs).Ok);
        Assert.NotEqual(first, theirs);
        s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db + "2", _db + "2-wal", _db + "2-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void rename_only_your_own_with_a_plain_name()
    {
        Assert.True(_s.SaveMap("Racer", null, "duel.lvl", [1, 2, 3, 4], out var id).Ok);
        Assert.False(_s.RenameMap("Other", id, "mine now").Ok);
        Assert.False(_s.RenameMap("Racer", id + 99, "nope").Ok);
        Assert.True(_s.RenameMap("RACER", id, "  <Duel arena v2>.lvl ").Ok);
        Assert.Equal("Duel arena v2", _s.Maps("Racer").Single().Name);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, _s.MapFile("Racer", id)!.Value.Data);
    }
}
