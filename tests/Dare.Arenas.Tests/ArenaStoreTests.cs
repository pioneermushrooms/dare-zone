using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class ArenaStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-arenas-{Guid.NewGuid():N}.db");
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

    private ArenaStore Owned(string arena = "bigarena", string owner = "Big Chris")
    {
        var s = Open();
        Assert.True(s.Request(owner, arena, "pitch").Ok);
        Assert.True(s.Approve(arena, "staff", NoFolder).Ok);
        return s;
    }

    [Fact]
    public void request_then_approve_makes_owner_and_events()
    {
        var s = Open();
        Assert.True(s.Request("chris", "myarena", "fun").Ok);
        Assert.Single(s.Requests(true, 10));
        _now = _now.AddHours(1);
        Assert.True(s.Approve("myarena", "staff", NoFolder).Ok);
        var a = s.Get("myarena")!;
        Assert.Equal("chris", a.Owner);
        Assert.Equal(ArenaStatus.Active, a.Status);
        Assert.Equal(_now, a.Created);
        Assert.Equal(_now, a.LastEntered);
        Assert.Empty(s.Requests(true, 10));
        Assert.Equal(new[] { "request", "approve" }, s.Events("myarena", 10).Select(e => e.Kind).OrderBy(k => k == "request" ? 0 : 1));
    }

    [Fact]
    public void owner_match_ignores_case_and_spaces_kept()
    {
        var s = Owned();
        Assert.Equal("Big Chris", s.Get("BIGARENA")!.Owner);
        Assert.Equal("owner", s.RoleIn("bigarena", "big chris"));
        Assert.Equal("bigarena", s.OwnedBy("BIG CHRIS")!.Name);
    }

    [Fact]
    public void one_pending_request_per_player()
    {
        var s = Open();
        Assert.True(s.Request("chris", "one", "x").Ok);
        Assert.False(s.Request("CHRIS", "two", "x").Ok);
    }

    [Fact]
    public void one_arena_per_player_and_names_unique()
    {
        var s = Owned("one", "chris");
        Assert.False(s.Request("Chris", "two", "x").Ok);
        Assert.False(s.Request("other", "ONE", "x").Ok);
        Assert.True(s.Request("other", "two", "x").Ok);
        Assert.False(s.Request("third", "two", "x").Ok); // pending name
    }

    [Fact]
    public void decline_needs_reason_and_is_public()
    {
        var s = Open();
        s.Request("chris", "one", "x");
        Assert.False(s.Decline("one", "staff", "  ").Ok);
        Assert.True(s.Decline("one", "staff", "too similar").Ok);
        var r = s.Requests(false, 10).Single();
        Assert.Equal(RequestStatus.Declined, r.Status);
        Assert.Equal("too similar", r.Reason);
        Assert.Contains(s.Events("one", 10), e => e.Kind == "decline" && e.Detail.Contains("too similar"));
        Assert.Null(s.Get("one"));
        Assert.True(s.Request("chris", "one", "again").Ok);
    }

    [Fact]
    public void approve_refuses_when_folder_exists()
    {
        var s = Open();
        s.Request("chris", "one", "x");
        var r = s.Approve("one", "staff", _ => true);
        Assert.False(r.Ok);
        Assert.Null(s.Get("one"));
        Assert.Single(s.Requests(true, 10));
        Assert.DoesNotContain(s.Events("one", 10), e => e.Kind == "approve");
    }

    [Fact]
    public void second_decision_is_rejected()
    {
        var a = Open();
        var b = Open();
        a.Request("chris", "one", "x");
        Assert.True(a.Approve("one", "s1", NoFolder).Ok);
        var r = b.Decline("one", "s2", "nope");
        Assert.False(r.Ok);
        Assert.Contains("already decided", r.Message);
        var r2 = b.Approve("one", "s2", NoFolder);
        Assert.False(r2.Ok);
        Assert.Contains("already decided", r2.Message);
    }

    [Fact]
    public void comod_limit_five_and_only_owner_adds()
    {
        var s = Owned("one", "chris");
        Assert.False(s.AddCoMod("one", "intruder", "p1").Ok);
        for (var i = 1; i <= 5; i++) Assert.True(s.AddCoMod("one", "CHRIS", $"p{i}").Ok);
        Assert.False(s.AddCoMod("one", "chris", "p6").Ok);
        Assert.False(s.AddCoMod("one", "chris", "P1").Ok);
        Assert.Equal("comod", s.RoleIn("one", "P3"));
        Assert.False(s.RemoveCoMod("one", "intruder", "p1").Ok);
        Assert.True(s.RemoveCoMod("one", "chris", "p1").Ok);
        Assert.False(s.RemoveCoMod("one", "chris", "p1").Ok);
        Assert.Null(s.RoleIn("one", "p1"));
        Assert.True(s.AddCoMod("one", "chris", "p6").Ok);
    }

    [Fact]
    public void owner_cannot_be_own_comod()
    {
        var s = Owned("one", "chris");
        Assert.False(s.AddCoMod("one", "chris", "CHRIS").Ok);
    }

    [Fact]
    public void archive_idle_respects_threshold()
    {
        var s = Owned("old", "a");
        _now = _now.AddDays(2);
        Assert.True(s.Request("b", "newer", "x").Ok);
        Assert.True(s.Approve("newer", "staff", NoFolder).Ok);
        _now = _now.AddDays(29); // old: 31d, newer: 29d
        var archived = s.ArchiveIdle(TimeSpan.FromDays(30));
        Assert.Equal(new[] { "old" }, archived);
        Assert.Equal(ArenaStatus.Archived, s.Get("old")!.Status);
        Assert.Equal(ArenaStatus.Active, s.Get("newer")!.Status);
        Assert.Single(s.Events("old", 20), e => e.Kind == "archive");
        Assert.Empty(s.ArchiveIdle(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void mark_entered_postpones_archive()
    {
        var s = Owned("one", "a");
        _now = _now.AddDays(25);
        s.MarkEntered("one");
        _now = _now.AddDays(25);
        Assert.Empty(s.ArchiveIdle(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void revive_returns_to_active_from_archived_and_suspended()
    {
        var s = Owned("one", "a");
        _now = _now.AddDays(40);
        s.ArchiveIdle(TimeSpan.FromDays(30));
        Assert.True(s.Revive("one", "staff").Ok);
        var a = s.Get("one")!;
        Assert.Equal(ArenaStatus.Active, a.Status);
        Assert.Equal(_now, a.LastEntered);
        Assert.False(s.Revive("one", "staff").Ok); // already active
        Assert.True(s.Suspend("one", "staff", "abuse").Ok);
        Assert.Equal(ArenaStatus.Suspended, s.Get("one")!.Status);
        Assert.True(s.Revive("one", "staff").Ok);
        Assert.Equal(ArenaStatus.Active, s.Get("one")!.Status);
    }

    [Fact]
    public void role_only_when_active()
    {
        var s = Owned("one", "a");
        s.AddCoMod("one", "a", "m");
        Assert.Equal("owner", s.RoleIn("one", "A"));
        Assert.Equal("comod", s.RoleIn("one", "M"));
        Assert.Null(s.RoleIn("one", "z"));
        Assert.Null(s.RoleIn("nope", "a"));
        s.Suspend("one", "staff", "why");
        Assert.Null(s.RoleIn("one", "a"));
        Assert.Null(s.RoleIn("one", "m"));
    }

    [Fact]
    public void suspend_requires_reason_and_active()
    {
        var s = Owned("one", "a");
        Assert.False(s.Suspend("one", "staff", "").Ok);
        Assert.True(s.Suspend("one", "staff", "r").Ok);
        Assert.False(s.Suspend("one", "staff", "r").Ok);
        Assert.False(s.Suspend("missing", "staff", "r").Ok);
    }

    [Fact]
    public void transfer_changes_owner_and_clears_nothing_else()
    {
        var s = Owned("one", "a");
        s.AddCoMod("one", "a", "m");
        s.AddCoMod("one", "a", "n");
        var before = s.Get("one")!;
        Assert.True(s.Transfer("one", "staff", "newguy").Ok);
        var a = s.Get("one")!;
        Assert.Equal("newguy", a.Owner);
        Assert.Equal(before.Created, a.Created);
        Assert.Equal(before.Status, a.Status);
        Assert.Equal(new[] { "m", "n" }, a.CoMods.OrderBy(x => x));
        Assert.Null(s.OwnedBy("a"));
    }

    [Fact]
    public void transfer_to_comod_removes_from_comods_and_rejects_existing_owner()
    {
        var s = Owned("one", "a");
        s.AddCoMod("one", "a", "m");
        Assert.True(s.Transfer("one", "staff", "M").Ok);
        Assert.Equal("M", s.Get("one")!.Owner);
        Assert.Empty(s.Get("one")!.CoMods);

        Assert.True(s.Request("c", "two", "x").Ok);
        Assert.True(s.Approve("two", "staff", NoFolder).Ok);
        Assert.False(s.Transfer("two", "staff", "m").Ok);
    }

    [Fact]
    public void every_mutation_appends_one_event()
    {
        var s = Owned("one", "a");
        int Count() => s.Events("one", 100).Count;
        var n = Count(); // request + approve
        Assert.Equal(2, n);
        s.AddCoMod("one", "a", "m"); Assert.Equal(++n, Count());
        s.RemoveCoMod("one", "a", "m"); Assert.Equal(++n, Count());
        s.Suspend("one", "st", "r"); Assert.Equal(++n, Count());
        s.Revive("one", "st"); Assert.Equal(++n, Count());
        s.Transfer("one", "st", "b"); Assert.Equal(++n, Count());
        Assert.Equal(new[] { "approve", "comod-add", "comod-remove", "request", "revive", "suspend", "transfer" },
            s.Events("one", 100).Select(e => e.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void data_persists_across_open()
    {
        Owned("one", "a").Dispose();
        var s2 = Open();
        Assert.Equal("a", s2.Get("one")!.Owner);
    }

    [Fact]
    public void staff_archive_moves_active_or_suspended_to_archived_with_public_event()
    {
        var s = Owned("one", "a");
        Assert.True(s.Archive("one", "Staffer").Ok);
        Assert.Equal(ArenaStatus.Archived, s.Get("one")!.Status);
        var e = s.Events("one", 10)[0];
        Assert.Equal("archive", e.Kind);
        Assert.Equal("Staffer", e.Actor);
        Assert.Null(s.RoleIn("one", "a"));

        Assert.False(s.Archive("one", "Staffer").Ok);      // already archived
        Assert.False(s.Archive("missing", "Staffer").Ok);

        Assert.True(s.Revive("one", "Staffer").Ok);
        Assert.True(s.Suspend("one", "Staffer", "r").Ok);
        Assert.True(s.Archive("one", "Staffer").Ok);       // suspended -> archived
        Assert.Equal(ArenaStatus.Archived, s.Get("one")!.Status);
        Assert.Equal("a", s.Get("one")!.Owner);             // ownership kept
    }

    [Fact]
    public void failed_archive_appends_no_event()
    {
        var s = Owned("one", "a");
        s.Archive("one", "st");
        var n = s.Events("one", 100).Count;
        s.Archive("one", "st");
        Assert.Equal(n, s.Events("one", 100).Count);
    }

    [Fact]
    public void roles_for_lists_active_arenas_where_player_is_owner_or_comod()
    {
        var s = Owned("one", "a");
        Assert.True(s.Request("b", "two", "x").Ok);
        Assert.True(s.Approve("two", "staff", NoFolder).Ok);
        Assert.True(s.Request("c", "three", "x").Ok);
        Assert.True(s.Approve("three", "staff", NoFolder).Ok);
        s.AddCoMod("two", "b", "A");
        s.AddCoMod("three", "c", "a");
        s.Suspend("three", "staff", "r");

        var roles = s.RolesFor("A");
        Assert.Equal(new[] { ("one", "owner"), ("two", "comod") }, roles.Select(r => (r.Arena, r.Role)));
        Assert.Empty(s.RolesFor("nobody"));
    }

    [Fact]
    public void arenas_lists_every_owned_arena_by_name()
    {
        var s = Owned("zeta", "a");
        Assert.True(s.Request("b", "alpha", "x").Ok);
        Assert.True(s.Approve("alpha", "staff", NoFolder).Ok);
        s.Suspend("zeta", "staff", "r");
        var all = s.Arenas();
        Assert.Equal(new[] { "alpha", "zeta" }, all.Select(a => a.Name));
        Assert.Equal(ArenaStatus.Suspended, all[1].Status);
    }
}
