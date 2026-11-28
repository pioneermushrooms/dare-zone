using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// A pilot's saved .lvz packages (the LVZ editor's library): private, capped, replaceable,
// renamable, duplicable, removable; names stay safe to use as an arena's LevelFiles entry.
public sealed class PilotLvzTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-lvz-{Guid.NewGuid():N}.db");
    private readonly ArenaStore _s;

    public PilotLvzTests() => _s = new ArenaStore(_db, () => new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Theory]
    [InlineData("deco.lvz", "deco.lvz")]
    [InlineData("My Deco (2)", "My_Deco__2.lvz")]
    [InlineData("../../etc/passwd", "passwd.lvz")]
    [InlineData("...", "graphics.lvz")]
    [InlineData(null, "graphics.lvz")]
    [InlineData("a-very-long-name-that-goes-on-and-on-forever.lvz", "a-very-long-name-that-goes-o.lvz")]
    public void file_names_are_safe_for_level_files(string? name, string expected)
    {
        var n = ArenaStore.LvzFileName(name);
        Assert.Equal(expected, n);
        Assert.True(LevelFiles.IsSafeFileName(n), n);
    }

    [Fact]
    public void save_open_replace_rename_duplicate_remove_only_your_own()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../js/fixtures/match.lvz"));
        Assert.True(UploadPolicy.TryCheck(UploadKind.Lvz, "match.lvz", bytes, 0, out _));
        Assert.True(_s.SaveLvz("Racer", null, "match", bytes, out var id).Ok);
        Assert.Equal("match.lvz", _s.LvzList("racer").Single().Name);
        Assert.Equal(bytes, _s.LvzFileOf("RACER", id)!.Value.Data);
        Assert.Null(_s.LvzFileOf("Other", id));
        Assert.False(_s.SaveLvz("Other", id, "x", bytes, out _).Ok);
        Assert.False(_s.RemoveLvz("Other", id).Ok);
        Assert.False(_s.RenameLvz("Other", id, "y").Ok);
        Assert.False(_s.DuplicateLvz("Other", id, out _).Ok);

        Assert.True(_s.SaveLvz("Racer", id, "match v2", [1, 2, 3], out var same).Ok);
        Assert.Equal(id, same);
        Assert.Equal("match_v2.lvz", _s.LvzList("Racer").Single().Name);
        Assert.True(_s.RenameLvz("Racer", id, "deco").Ok);
        Assert.True(_s.DuplicateLvz("Racer", id, out var copy).Ok);
        Assert.Equal("deco-copy.lvz", _s.LvzFileOf("Racer", copy)!.Value.Name);
        Assert.True(_s.RemoveLvz("Racer", id).Ok);
        Assert.Equal(copy, _s.LvzList("Racer").Single().Id);
    }

    [Fact]
    public void capped_per_pilot()
    {
        for (var i = 0; i < ArenaStore.MaxPilotLvz; i++) Assert.True(_s.SaveLvz("Racer", null, $"l{i}", [1], out _).Ok);
        Assert.False(_s.SaveLvz("Racer", null, "one more", [1], out _).Ok);
        Assert.False(_s.DuplicateLvz("Racer", _s.LvzList("Racer")[0].Id, out _).Ok);
        Assert.True(_s.SaveLvz("Other", null, "theirs", [1], out _).Ok);
    }
}
