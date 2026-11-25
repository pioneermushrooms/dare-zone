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
    public void refuses_owner_who_already_owns_one()
    {
        Assert.True(_s.Create("one", "S", "Racer", NoFolder, FolderOk).Ok);
        var r = _s.Create("two", "S", "RACER", NoFolder, FolderOk);
        Assert.False(r.Ok);
        Assert.Null(_s.Get("two"));
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
    public void refuses_owner_with_pending_request()
    {
        Assert.True(_s.Request("Asker", "wanted", "pitch").Ok);
        Assert.False(_s.Create("other", "S", "asker", NoFolder, FolderOk).Ok);
        Assert.Null(_s.Get("other"));
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
        Assert.True(_s.Create("one", "S", "A", NoFolder, FolderOk).Ok);
        var ran = false;
        Assert.False(_s.Create("two", "S", "A", NoFolder, () => { ran = true; return null; }).Ok);
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
        Assert.False(_s.Request("Racer", "another", "x").Ok); // still one arena per player
        Assert.Contains(("skyrace", "owner"), _s.RolesFor("racer"));
    }
}
