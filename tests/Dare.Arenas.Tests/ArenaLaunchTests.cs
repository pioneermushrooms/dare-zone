using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// ArenaStore.Launch: a player makes an arena themselves, no approval (?makearena, web hub).
public sealed class ArenaLaunchTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-launch-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly ArenaStore _s;

    public ArenaLaunchTests() => _s = new ArenaStore(_db, () => _now);

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private static bool NoFolder(string _) => false;
    private static string? FolderOk() => null;

    [Fact]
    public void launch_makes_the_player_owner_at_once_with_a_public_record()
    {
        var r = _s.Launch("skyrace", "Racer", "Fast laps", NoFolder, FolderOk);
        Assert.True(r.Ok, r.Message);
        var a = _s.Get("skyrace")!;
        Assert.Equal("Racer", a.Owner);
        Assert.Equal(ArenaStatus.Active, a.Status);
        Assert.Equal("owner", _s.RoleIn("skyrace", "racer"));

        var e = Assert.Single(_s.Events("skyrace", 10));
        Assert.Equal("launch", e.Kind);
        Assert.Equal("Racer", e.Actor);
        Assert.Equal("Fast laps", e.Detail);

        var req = Assert.Single(_s.Requests(pendingOnly: false, 10));
        Assert.Equal(RequestStatus.Approved, req.Status);
        Assert.Equal("Racer", req.DecidedBy);
        Assert.Equal("Fast laps", req.Pitch);
        Assert.Empty(_s.PendingRequestsOldestFirst(10));
    }

    [Fact]
    public void the_pitch_is_optional()
    {
        Assert.True(_s.Launch("skyrace", "Racer", "", NoFolder, FolderOk).Ok);
        Assert.Equal("", Assert.Single(_s.Events("skyrace", 10)).Detail);
    }

    [Fact]
    public void one_arena_per_player()
    {
        Assert.True(_s.Launch("skyrace", "Racer", "", NoFolder, FolderOk).Ok);
        var r = _s.Launch("other", "RACER", "", NoFolder, FolderOk);
        Assert.False(r.Ok);
        Assert.Contains("already own", r.Message);
        Assert.Null(_s.Get("other"));
    }

    [Fact]
    public void a_taken_name_is_refused_whatever_the_case()
    {
        Assert.True(_s.Launch("skyrace", "Racer", "", NoFolder, FolderOk).Ok);
        Assert.False(_s.Launch("SkyRace", "Other", "", NoFolder, FolderOk).Ok);
        Assert.Equal("Racer", _s.Get("skyrace")!.Owner);
    }

    [Fact]
    public void someone_elses_old_pending_request_keeps_the_name()
    {
        Assert.True(_s.Request("Asker", "skyrace", "mine first").Ok);
        var r = _s.Launch("skyrace", "Other", "", NoFolder, FolderOk);
        Assert.False(r.Ok);
        Assert.Contains("Someone already asked", r.Message);
        Assert.Null(_s.Get("skyrace"));
        Assert.Single(_s.PendingRequestsOldestFirst(10));
    }

    [Fact]
    public void the_players_own_old_request_for_the_same_name_is_approved_not_duplicated()
    {
        Assert.True(_s.Request("Asker", "skyrace", "old pitch").Ok);
        Assert.True(_s.Launch("skyrace", "asker", "new pitch", NoFolder, FolderOk).Ok);
        var req = Assert.Single(_s.Requests(pendingOnly: false, 10));
        Assert.Equal(RequestStatus.Approved, req.Status);
        Assert.Equal("old pitch", req.Pitch);
        Assert.Equal("Asker", _s.Get("skyrace")!.Owner, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void the_players_own_old_request_for_another_name_is_withdrawn()
    {
        Assert.True(_s.Request("Asker", "oldname", "old").Ok);
        Assert.True(_s.Launch("newname", "Asker", "", NoFolder, FolderOk).Ok);
        var all = _s.Requests(pendingOnly: false, 10);
        var old = all.Single(x => x.Arena == "oldname");
        Assert.Equal(RequestStatus.Declined, old.Status);
        Assert.Equal("withdrawn: launched newname", old.Reason);
        Assert.Empty(_s.PendingRequestsOldestFirst(10));
        // the old name is free again for anyone
        Assert.True(_s.Launch("oldname", "Someone", "", NoFolder, FolderOk).Ok);
    }

    [Fact]
    public void an_existing_folder_refuses_and_never_calls_create()
    {
        var called = false;
        var r = _s.Launch("skyrace", "Racer", "", _ => true, () => { called = true; return null; });
        Assert.False(r.Ok);
        Assert.False(called);
        Assert.Null(_s.Get("skyrace"));
    }

    [Fact]
    public void a_folder_error_rolls_everything_back()
    {
        Assert.True(_s.Request("Racer", "oldname", "old").Ok);
        var r = _s.Launch("skyrace", "Racer", "", NoFolder, () => "disk full");
        Assert.False(r.Ok);
        Assert.Contains("disk full", r.Message);
        Assert.Null(_s.Get("skyrace"));
        Assert.Empty(_s.Events("skyrace", 10));
        Assert.Single(_s.PendingRequestsOldestFirst(10)); // the old request is untouched
    }

    [Fact]
    public void blank_player_or_name_is_refused()
    {
        Assert.False(_s.Launch("", "Racer", "", NoFolder, FolderOk).Ok);
        Assert.False(_s.Launch("skyrace", " ", "", NoFolder, FolderOk).Ok);
    }
}
