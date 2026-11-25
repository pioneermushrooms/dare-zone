using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// Owners switch an arena between public and private whenever they like, and can discard it.
public sealed class ArenaVisibilityTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-visibility-{Guid.NewGuid():N}.db");
    private readonly ArenaStore _s;

    public ArenaVisibilityTests()
    {
        _s = new ArenaStore(_db, () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        Assert.True(_s.Create("skyrace", "staff", "Racer", _ => false, () => null).Ok);
    }

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void public_by_default_and_the_owner_switches_it_either_way()
    {
        Assert.False(_s.IsPrivate("skyrace"));
        Assert.True(_s.SetPrivate("skyrace", "racer", true).Ok);
        Assert.True(_s.IsPrivate("skyrace"));
        Assert.Contains("skyrace", _s.PrivateArenas());
        Assert.True(_s.SetPrivate("skyrace", "Racer", false).Ok);
        Assert.False(_s.IsPrivate("skyrace"));
        Assert.Contains(_s.Events("skyrace", 10), e => e.Kind == "private");
    }

    [Fact]
    public void only_the_owner_switches_or_discards()
    {
        Assert.True(_s.AddCoMod("skyrace", "Racer", "Helper").Ok);
        Assert.False(_s.SetPrivate("skyrace", "Helper", true).Ok);
        Assert.False(_s.Discard("skyrace", "Helper").Ok);
        Assert.False(_s.Discard("nosuch", "Racer").Ok);
        Assert.Equal(ArenaStatus.Active, _s.Get("skyrace")!.Status);
    }

    [Fact]
    public void discarding_archives_it_and_frees_the_owners_slot()
    {
        Assert.True(_s.Discard("skyrace", "Racer").Ok);
        Assert.Equal(ArenaStatus.Archived, _s.Get("skyrace")!.Status);
        Assert.Contains("skyrace", _s.PendingArchiveMoves());
        Assert.False(_s.Discard("skyrace", "Racer").Ok);
        Assert.False(_s.SetPrivate("skyrace", "Racer", true).Ok);
        Assert.Null(_s.RoleIn("skyrace", "racer"));
    }
}
