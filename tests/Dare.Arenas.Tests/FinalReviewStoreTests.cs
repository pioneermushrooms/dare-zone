using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

/// <summary>Store rules added after the final whole-branch review.</summary>
public sealed class FinalReviewStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-final-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ArenaStore> _stores = new();

    private ArenaStore Open()
    {
        var s = new ArenaStore(_db, () => _now);
        _stores.Add(s);
        return s;
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    private static bool NoFolder(string _) => false;

    private ArenaStore Owned(string arena = "one", string owner = "chris")
    {
        var s = Open();
        Assert.True(s.Request(owner, arena, "pitch").Ok);
        Assert.True(s.Approve(arena, "staff", NoFolder).Ok);
        return s;
    }

    // ---- approval creates the folder inside the transaction ------------------------------------

    [Fact]
    public void approve_runs_folder_creation_and_commits_when_it_succeeds()
    {
        var s = Open();
        s.Request("chris", "one", "x");
        var created = 0;
        var r = s.Approve("one", "staff", NoFolder, () => { created++; return null; });
        Assert.True(r.Ok);
        Assert.Equal(1, created);
        Assert.Equal(ArenaStatus.Active, s.Get("one")!.Status);
    }

    [Fact]
    public void approve_is_rolled_back_when_the_folder_cannot_be_created()
    {
        var s = Open();
        s.Request("chris", "one", "x");
        var r = s.Approve("one", "staff", NoFolder, () => "template folder is missing");
        Assert.False(r.Ok);
        Assert.Contains("template folder is missing", r.Message);
        Assert.Null(s.Get("one"));                       // no Active arena without a folder
        Assert.Single(s.Requests(true, 10));              // still pending, can be approved later
        Assert.DoesNotContain(s.Events("one", 10), e => e.Kind == "approve");
        Assert.True(s.Approve("one", "staff", NoFolder, () => null).Ok);
    }

    [Fact]
    public void folder_is_not_created_when_the_approval_is_refused()
    {
        var s = Open();
        s.Request("chris", "one", "x");
        var created = 0;
        Assert.False(s.Approve("one", "staff", _ => true, () => { created++; return null; }).Ok);
        Assert.False(s.Approve("nope", "staff", NoFolder, () => { created++; return null; }).Ok);
        Assert.Equal(0, created);
    }

    // ---- self-approval ----------------------------------------------------------------------------

    [Fact]
    public void staff_cannot_approve_or_decline_their_own_request()
    {
        var s = Open();
        s.Request("Moddy", "moddyland", "x");
        var a = s.Approve("moddyland", "moddy", NoFolder);
        Assert.False(a.Ok);
        Assert.Contains("own request", a.Message);
        var d = s.Decline("moddyland", "MODDY", "changed my mind");
        Assert.False(d.Ok);
        Assert.Contains("own request", d.Message);
        Assert.Single(s.Requests(true, 10));
        Assert.True(s.Approve("moddyland", "Tester", NoFolder).Ok);
    }

    // ---- revive needs a folder --------------------------------------------------------------------

    [Fact]
    public void revive_fails_cleanly_when_the_folder_cannot_be_restored()
    {
        var s = Owned();
        Assert.True(s.Archive("one", "staff").Ok);
        var r = s.Revive("one", "staff", () => "no archived folder found");
        Assert.False(r.Ok);
        Assert.Contains("no archived folder found", r.Message);
        Assert.Equal(ArenaStatus.Archived, s.Get("one")!.Status);
        Assert.DoesNotContain(s.Events("one", 10), e => e.Kind == "revive");
    }

    [Fact]
    public void revive_of_suspended_arena_without_folder_fails_too()
    {
        var s = Owned();
        Assert.True(s.Suspend("one", "staff", "why").Ok);
        Assert.False(s.Revive("one", "staff", () => "arenas/one/ is missing").Ok);
        Assert.Equal(ArenaStatus.Suspended, s.Get("one")!.Status);
        Assert.True(s.Revive("one", "staff", () => null).Ok);
        Assert.Equal(ArenaStatus.Active, s.Get("one")!.Status);
    }

    [Fact]
    public void revive_does_not_touch_the_folder_of_an_active_arena()
    {
        var s = Owned();
        var called = 0;
        Assert.False(s.Revive("one", "staff", () => { called++; return null; }).Ok);
        Assert.Equal(0, called);
    }

    // ---- archive folder moves are deferred to the next start ------------------------------------------

    [Fact]
    public void staff_archive_records_a_pending_folder_move()
    {
        var s = Owned();
        Assert.Empty(s.PendingArchiveMoves());
        Assert.True(s.Archive("one", "staff").Ok);
        Assert.Equal(new[] { "one" }, s.PendingArchiveMoves());
    }

    [Fact]
    public void idle_archive_records_pending_moves()
    {
        var s = Owned("old", "a");
        _now = _now.AddDays(31);
        Assert.Equal(new[] { "old" }, s.ArchiveIdle(TimeSpan.FromDays(30)));
        Assert.Equal(new[] { "old" }, s.PendingArchiveMoves());
    }

    [Fact]
    public void revive_before_the_move_cancels_it()
    {
        var s = Owned();
        s.Archive("one", "staff");
        Assert.True(s.Revive("one", "staff", () => null).Ok);
        Assert.Empty(s.PendingArchiveMoves());
    }

    [Fact]
    public void completed_move_is_forgotten_and_survives_reopen()
    {
        var s = Owned();
        s.Archive("one", "staff");
        s.Dispose();
        var t = Open();
        Assert.Equal(new[] { "one" }, t.PendingArchiveMoves());
        t.ArchiveMoveDone("ONE");
        Assert.Empty(t.PendingArchiveMoves());
    }

    [Fact]
    public void failed_archive_records_no_move()
    {
        var s = Owned();
        s.Archive("one", "staff");
        s.ArchiveMoveDone("one");
        Assert.False(s.Archive("one", "staff").Ok); // already archived
        Assert.Empty(s.PendingArchiveMoves());
    }

    // ---- idle archive skips arenas with players ---------------------------------------------------------

    [Fact]
    public void idle_archive_skips_occupied_arenas()
    {
        var s = Owned("old", "a");
        Assert.True(s.Request("b", "older", "x").Ok);
        Assert.True(s.Approve("older", "staff", NoFolder).Ok);
        _now = _now.AddDays(31);
        var archived = s.ArchiveIdle(TimeSpan.FromDays(30), name => name == "old");
        Assert.Equal(new[] { "older" }, archived);
        Assert.Equal(ArenaStatus.Active, s.Get("old")!.Status);
        Assert.DoesNotContain(s.Events("old", 10), e => e.Kind == "archive");
    }

    // ---- pending requests: oldest first --------------------------------------------------------------

    [Fact]
    public void pending_requests_oldest_first_returns_the_longest_waiting()
    {
        var s = Open();
        for (var i = 0; i < 12; i++)
        {
            Assert.True(s.Request($"p{i}", $"arena{(char)('a' + i)}", "x").Ok);
            _now = _now.AddMinutes(1);
        }
        var oldest = s.PendingRequestsOldestFirst(10);
        Assert.Equal(10, oldest.Count);
        Assert.Equal("p0", oldest[0].Player);
        Assert.Equal("p9", oldest[9].Player);
    }
}
