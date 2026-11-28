using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// A pilot's named settings sets: private, capped, checked, replaceable, renamable, removable.
public sealed class PilotSettingSetsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-settingsets-{Guid.NewGuid():N}.db");
    private readonly ArenaStore _s;

    public PilotSettingSetsTests() => _s = new ArenaStore(_db, () => new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private static Dictionary<string, string> Set(params (string K, string V)[] kv) => kv.ToDictionary(p => p.K, p => p.V);

    [Fact]
    public void saves_lists_and_reads_back_a_set()
    {
        var r = _s.SaveSettingSet("Pilot", null, "Big bombs", Set(("Warbird:MaxBombs", " 3 "), ("Flag:CarryFlags", "2")), out var id);
        Assert.True(r.Ok, r.Message);
        var list = _s.SettingSets("pilot");
        Assert.Single(list);
        Assert.Equal(("Big bombs", 2), (list[0].Name, list[0].Count));
        var got = _s.SettingSetOf("PILOT", id)!.Value;
        Assert.Equal("3", got.Settings["Warbird:MaxBombs"]); // trimmed like a single edit
        Assert.Null(_s.SettingSetOf("Other", id));
        Assert.Empty(_s.SettingSets("Other"));
    }

    [Fact]
    public void refuses_bad_names_and_values()
    {
        Assert.False(_s.SaveSettingSet("P", null, "x", Set(), out _).Ok);
        Assert.False(_s.SaveSettingSet("P", null, "x", Set(("NoColon", "1")), out _).Ok);
        Assert.False(_s.SaveSettingSet("P", null, "x", Set(("A:B", "1;2")), out _).Ok);
        Assert.False(_s.SaveSettingSet("P", null, "x", Set(("A:B", "")), out _).Ok);
        Assert.True(_s.SaveSettingSet("P", null, "x", Set(("Misc:GreetMessage", "")), out _).Ok); // an empty message is "off"
        var big = Enumerable.Range(0, ArenaStore.MaxSettingsPerSet + 1).ToDictionary(i => $"S:K{i}", _ => "1");
        Assert.False(_s.SaveSettingSet("P", null, "x", big, out _).Ok);
    }

    [Fact]
    public void replaces_renames_and_removes_only_your_own()
    {
        _s.SaveSettingSet("P", null, "one", Set(("A:B", "1")), out var id);
        Assert.False(_s.SaveSettingSet("Q", id, "mine now", Set(("A:B", "2")), out _).Ok);
        Assert.True(_s.SaveSettingSet("P", id, "one v2", Set(("A:B", "2"), ("A:C", "3")), out var same).Ok);
        Assert.Equal(id, same);
        Assert.Equal("one v2", _s.SettingSetOf("P", id)!.Value.Name);
        Assert.Equal(2, _s.SettingSets("P")[0].Count);

        Assert.False(_s.RenameSettingSet("Q", id, "x").Ok);
        Assert.True(_s.RenameSettingSet("P", id, "  Renamed <b>!</b> ").Ok);
        Assert.Equal("Renamed bb", _s.SettingSetOf("P", id)!.Value.Name);

        Assert.False(_s.RemoveSettingSet("Q", id).Ok);
        Assert.True(_s.RemoveSettingSet("P", id).Ok);
        Assert.Empty(_s.SettingSets("P"));
    }

    [Fact]
    public void caps_sets_per_pilot()
    {
        for (var i = 0; i < ArenaStore.MaxPilotSettingSets; i++)
            Assert.True(_s.SaveSettingSet("P", null, $"s{i}", Set(("A:B", "1")), out _).Ok);
        Assert.False(_s.SaveSettingSet("P", null, "one more", Set(("A:B", "1")), out _).Ok);
        Assert.True(_s.SaveSettingSet("Q", null, "other pilot", Set(("A:B", "1")), out _).Ok);
    }
}
