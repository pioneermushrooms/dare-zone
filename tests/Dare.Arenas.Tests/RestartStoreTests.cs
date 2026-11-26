using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// Scheduled restarts survive reopening the store (a server restart); one per arena.
public sealed class RestartStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-restarts-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset T = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void plans_are_kept_replaced_and_removed()
    {
        using (var s = new ArenaStore(_db, () => T))
        {
            s.SaveRestart(new("skyrace", T.AddMinutes(5), 60, "New map", "Racer"));
            s.SaveRestart(new("SKYRACE", T.AddMinutes(9), 0, null, "Racer"));
            s.SaveRestart(new("duelpit", T.AddHours(2), 300, "Maintenance", "Owner"));
        }
        using (var s = new ArenaStore(_db, () => T))
        {
            var plans = s.Restarts();
            Assert.Equal(2, plans.Count);
            Assert.Equal(new ScheduledRestart("skyrace", T.AddMinutes(9), 0, null, "Racer"), plans[0] with { Arena = "skyrace" });
            Assert.Equal("Maintenance", plans[1].Message);
            s.RemoveRestart("Skyrace");
            Assert.Single(s.Restarts());
        }
    }
}
