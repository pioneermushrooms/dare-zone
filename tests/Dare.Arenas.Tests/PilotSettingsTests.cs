using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Each pilot name's keys and macros: only [keys] and [macros] are kept, bounded, per name.
public sealed class PilotSettingsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-settings-{Guid.NewGuid():N}.db");
    private readonly AccountStore _s;
    private readonly Account _dare;

    public PilotSettingsTests()
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

    [Fact]
    public void keeps_only_keys_and_macros()
    {
        const string text = "[video]\nscale = 8\n[keys]\ngun = KeyA\n\n[audio]\nmute = true\n[macros]\nF9 = ;2;hello\n";
        Assert.True(_s.SavePilotSettings(_dare.Id, text).Ok);
        Assert.Equal("[keys]\ngun = KeyA\n[macros]\nF9 = ;2;hello\n", _s.PilotSettings(_dare.Id));
    }

    [Fact]
    public void empty_goes_back_to_defaults_and_limits_hold()
    {
        Assert.True(_s.SavePilotSettings(_dare.Id, "[keys]\ngun = KeyA\n").Ok);
        Assert.True(_s.SavePilotSettings(_dare.Id, "").Ok);
        Assert.Equal("", _s.PilotSettings(_dare.Id));
        Assert.False(_s.SavePilotSettings(_dare.Id, "[keys]\n" + new string('x', AccountStore.MaxSettingsBytes)).Ok);
        Assert.False(_s.SavePilotSettings(_dare.Id, string.Join("\n", Enumerable.Repeat("gun = KeyA", AccountStore.MaxSettingsLines + 1))).Ok);
        Assert.False(_s.SavePilotSettings(_dare.Id, "[macros]\nF9 = a\u0001b\n").Ok);
    }

    [Fact]
    public void each_name_has_its_own_and_a_deleted_name_takes_them_along()
    {
        Assert.True(_s.AddPilot(_dare.Id, "nerd", out var nerd).Ok);
        Assert.True(_s.SavePilotSettings(_dare.Id, "[keys]\ngun = KeyA\n").Ok);
        Assert.True(_s.SavePilotSettings(nerd!.Id, "[keys]\ngun = KeyB\n").Ok);
        Assert.Contains("KeyA", _s.PilotSettings(_dare.Id));
        Assert.Contains("KeyB", _s.PilotSettings(nerd.Id));
        Assert.True(_s.DeletePilot(_dare.Id, "nerd", out _).Ok);
        Assert.Equal("", _s.PilotSettings(nerd.Id));
    }
}
