using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// ArenaStore.Create: staff create an arena directly (?createarena, web staff view).
public sealed class ArenaCreateTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-create-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly ArenaStore _s;

    public ArenaCreateTests() => _s = new ArenaStore(_db, () => _now);

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
    public void create_makes_active_owner_and_one_public_event()
    {
        var r = _s.Create("skyrace", "Staffer", "Racer", NoFolder, FolderOk);
        Assert.True(r.Ok, r.Message);
        var a = _s.Get("skyrace")!;
        Assert.Equal("Racer", a.Owner);
        Assert.Equal(ArenaStatus.Active, a.Status);
        Assert.Equal(_now, a.Created);
        Assert.Equal("owner", _s.RoleIn("skyrace", "racer"));
        var e = Assert.Single(_s.Events("skyrace", 10));
        Assert.Equal("create", e.Kind);
        Assert.Equal("Staffer", e.Actor);
        Assert.Equal("owner: Racer", e.Detail);
        Assert.Empty(_s.Requests(pendingOnly: false, 10)); // no request round trip
    }

    [Fact]
    public void staff_may_create_for_themselves()
    {
        Assert.True(_s.Create("mine", "Staffer", "Staffer", NoFolder, FolderOk).Ok);
        Assert.Equal("owner", _s.RoleIn("mine", "staffer"));
    }

    [Fact]
    public void refuses_taken_name_any_case()
    {
        Assert.True(_s.Create("skyrace", "S", "A", NoFolder, FolderOk).Ok);
        var r = _s.Create("SKYRACE", "S", "B", NoFolder, FolderOk);
        Assert.False(r.Ok);
        Assert.Contains("taken", r.Message);
        Assert.Equal("A", _s.Get("skyrace")!.Owner);
    }

    [Fact]
    public void refuses_owner_who_already_owns_three()
    {
        foreach (var n in new[] { "one", "two", "three" })
            Assert.True(_s.Create(n, "S", "Racer", NoFolder, FolderOk).Ok);
        var r = _s.Create("four", "S", "RACER", NoFolder, FolderOk);
        Assert.False(r.Ok);
        Assert.Contains("3", r.Message);
    }

    [Fact]
    public void a_created_arena_gets_its_live_draft()
    {
        Assert.True(_s.Create("skyrace", "S", "Racer", NoFolder, FolderOk).Ok);
        var d = _s.Draft("skyrace")!;
        Assert.True(d.Live);
        Assert.Equal("Racer", d.Owner);
        Assert.Equal("owner", _s.RoleIn("#skyrace", "racer"));
        Assert.True(_s.IsLiveWithDraft("skyrace"));
    }

    [Fact]
    public void refuses_a_name_held_by_a_draft()
    {
        Assert.True(_s.CreateDraft("Pilot", "skyrace", NoFolder, FolderOk).Ok);
        Assert.False(_s.Create("skyrace", "S", "Other", NoFolder, FolderOk).Ok);
    }

    [Fact]
    public void refuses_name_with_pending_request()
    {
        Assert.True(_s.Request("Asker", "wanted", "pitch").Ok);
        var r = _s.Create("wanted", "S", "Other", NoFolder, FolderOk);
        Assert.False(r.Ok);
        Assert.Contains("pending request", r.Message);
        Assert.Null(_s.Get("wanted"));
    }

    [Fact]
    public void refuses_existing_folder_and_never_runs_create()
    {
        var ran = false;
        var r = _s.Create("skyrace", "S", "A", _ => true, () => { ran = true; return null; });
        Assert.False(r.Ok);
        Assert.False(ran);
        Assert.Null(_s.Get("skyrace"));
    }

    [Fact]
    public void folder_failure_rolls_back_everything()
    {
        var r = _s.Create("skyrace", "S", "A", NoFolder, () => "disk full");
        Assert.False(r.Ok);
        Assert.Contains("disk full", r.Message);
        Assert.Null(_s.Get("skyrace"));
        Assert.Empty(_s.Events(null, 10));
    }

    [Fact]
    public void checks_run_before_folder_is_created()
    {
        foreach (var n in new[] { "one", "two", "three" })
            Assert.True(_s.Create(n, "S", "A", NoFolder, FolderOk).Ok);
        var ran = false;
        Assert.False(_s.Create("four", "S", "A", NoFolder, () => { ran = true; return null; }).Ok);
        Assert.False(ran); // owner check failed first: no orphan folder
    }

    [Theory]
    [InlineData("", "S", "A")]
    [InlineData("x", "", "A")]
    [InlineData("x", "S", " ")]
    public void requires_all_names(string arena, string staff, string owner) =>
        Assert.False(_s.Create(arena, staff, owner, NoFolder, FolderOk).Ok);

    [Fact]
    public void created_arena_behaves_like_approved_one()
    {
        Assert.True(_s.Create("skyrace", "S", "Racer", NoFolder, FolderOk).Ok);
        Assert.True(_s.AddCoMod("skyrace", "Racer", "Helper").Ok);
        Assert.Equal("comod", _s.RoleIn("skyrace", "helper"));
        Assert.True(_s.Request("Racer", "another", "x").Ok); // up to three per player
        Assert.Contains(("skyrace", "owner"), _s.RolesFor("racer"));
    }
}
