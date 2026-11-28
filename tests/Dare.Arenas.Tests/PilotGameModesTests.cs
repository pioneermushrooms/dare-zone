using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// An account's game modes: keyed by person (every pilot name on the account), capped, checked,
// replaceable, renamable, duplicable, removable.
public sealed class PilotGameModesTests : IDisposable
{
    private const string V1 = "{\"version\":1}";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-gamemodes-{Guid.NewGuid():N}.db");
    private readonly ArenaStore _s;

    public PilotGameModesTests() => _s = new ArenaStore(_db, () => new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private static Dictionary<string, string> Set(params (string K, string V)[] kv) => kv.ToDictionary(p => p.K, p => p.V);

    [Fact]
    public void saves_lists_and_reads_back_a_mode()
    {
        var r = _s.SaveGameMode(7, "Chris", null, "Fast CTF", V1, Set(("Warbird:InitialGuns", "2")), out var id);
        Assert.True(r.Ok, r.Message);
        var list = _s.GameModes(7);
        Assert.Equal(("Fast CTF", "Chris", 1), (list.Single().Name, list[0].MadeBy, list[0].Count));
        Assert.Equal("2", _s.GameModeOf(7, id)!.Value.Settings["Warbird:InitialGuns"]);
        Assert.Equal(V1, _s.GameModeOf(7, id)!.Value.Data);
    }

    [Fact]
    public void second_name_on_same_person_sees_mode()
    {
        // The store keys on person only; made_by never restricts access.
        _s.SaveGameMode(7, "Chris", null, "M", V1, Set(("Warbird:InitialGuns", "2")), out var id);
        Assert.NotNull(_s.GameModeOf(7, id));
        Assert.Null(_s.GameModeOf(8, id));
        Assert.Empty(_s.GameModes(8));
        Assert.False(_s.RenameGameMode(8, id, "x").Ok);
        Assert.False(_s.RemoveGameMode(8, id).Ok);
        Assert.False(_s.DuplicateGameMode(8, id, out _).Ok);
        Assert.False(_s.SaveGameMode(8, "Other", id, "x", V1, Set(("Warbird:InitialGuns", "1")), out _).Ok);
        Assert.Equal("2", _s.GameModeOf(7, id)!.Value.Settings["Warbird:InitialGuns"]);
    }

    [Fact]
    public void refuses_bad_or_oversized_data()
    {
        var s = Set(("Warbird:InitialGuns", "2"));
        Assert.False(_s.SaveGameMode(7, "C", null, "M", "", s, out _).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "M", "[1]", s, out _).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "M", "{\"x\":1}", s, out _).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "M", "{\"version\":0}", s, out _).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "M", "{\"version\":\"1\"}", s, out _).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "M", "{not json", s, out _).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "M", "{\"version\":1,\"p\":\"" + new string('a', 70_000) + "\"}", s, out _).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "M", V1, Set(("Bad", "1")), out _).Ok);
        _s.SaveGameMode(7, "C", null, "M", V1, s, out var id);
        Assert.False(_s.SaveGameMode(7, "C", id, "M", "[1]", s, out _).Ok);
        Assert.Equal(V1, _s.GameModeOf(7, id)!.Value.Data);
    }

    [Fact]
    public void caps_at_25_per_person_including_duplicates()
    {
        long last = 0;
        for (var i = 0; i < ArenaStore.MaxPilotGameModes; i++)
            Assert.True(_s.SaveGameMode(7, "C", null, $"m{i}", V1, Set(("Warbird:InitialGuns", "1")), out last).Ok);
        Assert.False(_s.SaveGameMode(7, "C", null, "m25", V1, Set(("Warbird:InitialGuns", "1")), out _).Ok);
        Assert.False(_s.DuplicateGameMode(7, last, out _).Ok);
        Assert.True(_s.SaveGameMode(7, "C", last, "replaced", V1, Set(("Warbird:InitialGuns", "3")), out _).Ok); // replacing is fine at the cap
        Assert.True(_s.SaveGameMode(8, "D", null, "other", V1, Set(("Warbird:InitialGuns", "1")), out _).Ok);
    }

    [Fact]
    public void rename_duplicate_remove()
    {
        _s.SaveGameMode(7, "C", null, "Duel", V1, Set(("Warbird:InitialGuns", "1")), out var id);
        Assert.True(_s.RenameGameMode(7, id, "Duel 4v4!").Ok);
        Assert.Equal("Duel 4v4", _s.GameModeOf(7, id)!.Value.Name);
        Assert.True(_s.DuplicateGameMode(7, id, out var copy).Ok);
        Assert.Equal("Duel 4v4 (copy)", _s.GameModeOf(7, copy)!.Value.Name);
        Assert.Equal("1", _s.GameModeOf(7, copy)!.Value.Settings["Warbird:InitialGuns"]);
        Assert.True(_s.RemoveGameMode(7, id).Ok);
        Assert.Null(_s.GameModeOf(7, id));
        Assert.Single(_s.GameModes(7));
    }
}
