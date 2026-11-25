using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class DraftStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-drafts-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
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

    /// <summary>Makes the database look as it did before drafts existed, for one arena.</summary>
    private void ForgetDraft(string name)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM drafts WHERE name = $n; DELETE FROM comods WHERE arena = '#' || $n;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }
    private static string? Made() => null;
    private static bool HasAccount(string _) => true;

    private ArenaStore WithDraft(string owner = "Pilot", string name = "skyrace")
    {
        var s = Open();
        Assert.True(s.CreateDraft(owner, name, NoFolder, Made).Ok);
        return s;
    }

    [Fact]
    public void create_draft_makes_owner_role_in_hash_arena()
    {
        var s = WithDraft();
        var d = s.Draft("#SkyRace");
        Assert.NotNull(d);
        Assert.Equal("skyrace", d!.Name);
        Assert.Equal(DraftStatus.Active, d.Status);
        Assert.False(d.Live);
        Assert.Equal("owner", s.RoleIn("#skyrace", "pilot"));
        Assert.Null(s.RoleIn("#skyrace", "someone"));
        Assert.Null(s.RoleIn("skyrace", "pilot"));
        Assert.Equal("skyrace", s.DraftOwnedBy("PILOT")!.Name);
        Assert.Contains(s.Events("#skyrace", 10), e => e.Kind == "draft");
    }

    [Fact]
    public void create_draft_twice_same_name_one_wins()
    {
        var a = Open();
        var b = Open();
        Assert.True(a.CreateDraft("One", "skyrace", NoFolder, Made).Ok);
        var second = b.CreateDraft("Two", "SKYRACE", NoFolder, Made);
        Assert.False(second.Ok);
        Assert.Contains("taken", second.Message);
    }

    [Fact]
    public void create_draft_failing_folder_leaves_nothing()
    {
        var s = Open();
        Assert.False(s.CreateDraft("Pilot", "skyrace", NoFolder, () => "disk full").Ok);
        Assert.Null(s.Draft("skyrace"));
        Assert.False(s.CreateDraft("Pilot", "skyrace", n => n == "#skyrace", Made).Ok);
    }

    [Fact]
    public void one_draft_per_pilot()
    {
        var s = WithDraft();
        Assert.False(s.CreateDraft("pilot", "other", NoFolder, Made).Ok);
    }

    [Fact]
    public void draft_name_blocks_public_request_and_back()
    {
        var s = WithDraft();
        Assert.False(s.Request("Someone", "skyrace", "pitch").Ok);
        Assert.True(s.Request("Someone", "duelzone", "pitch").Ok);
        Assert.False(s.CreateDraft("Third", "duelzone", NoFolder, Made).Ok);
        // the draft's owner can't make an old-style request for another name
        Assert.False(s.Request("Pilot", "elsewhere", "pitch").Ok);
    }

    [Fact]
    public void invite_cap_is_five_and_needs_account()
    {
        var s = WithDraft();
        Assert.False(s.Invite("skyrace", "pilot", "ghost", _ => false).Ok);
        Assert.False(s.Invite("skyrace", "intruder", "friend", HasAccount).Ok);
        Assert.False(s.Invite("skyrace", "pilot", "Pilot", HasAccount).Ok);
        for (var i = 0; i < 5; i++) Assert.True(s.Invite("skyrace", "pilot", $"f{i}", HasAccount).Ok);
        Assert.False(s.Invite("skyrace", "pilot", "f9", HasAccount).Ok);
        Assert.True(s.IsInvited("#skyrace", "F0"));
        Assert.True(s.Uninvite("skyrace", "pilot", "f0").Ok);
        Assert.False(s.IsInvited("skyrace", "f0"));
        Assert.Equal(4, s.Draft("skyrace")!.Invited.Count);
    }

    [Fact]
    public void draft_comods_work_through_the_hash_arena()
    {
        var s = WithDraft();
        Assert.True(s.AddCoMod("#skyrace", "pilot", "Helper").Ok);
        Assert.Equal("comod", s.RoleIn("#skyrace", "helper"));
        Assert.True(s.RemoveCoMod("#skyrace", "pilot", "helper").Ok);
        Assert.Null(s.RoleIn("#skyrace", "helper"));
    }

    [Fact]
    public void request_public_links_draft_and_approve_sets_live()
    {
        var s = WithDraft();
        Assert.True(s.AddCoMod("#skyrace", "pilot", "Helper").Ok);
        Assert.True(s.RequestPublic("pilot", "fast laps").Ok);
        Assert.Equal(DraftStatus.Requested, s.Draft("skyrace")!.Status);
        Assert.False(s.RequestPublic("pilot", "again").Ok);
        Assert.True(s.IsDraftRequest("skyrace"));
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        var d = s.Draft("skyrace")!;
        Assert.Equal(DraftStatus.Published, d.Status);
        Assert.True(d.Live);
        Assert.Equal("owner", s.RoleIn("skyrace", "pilot"));
        Assert.Equal("comod", s.RoleIn("skyrace", "helper"));
        Assert.Equal("owner", s.RoleIn("#skyrace", "pilot"));
        Assert.True(s.IsLiveWithDraft("SkyRace"));
    }

    [Fact]
    public void decline_returns_draft_to_active()
    {
        var s = WithDraft();
        Assert.True(s.RequestPublic("pilot", "fast laps").Ok);
        Assert.True(s.Decline("skyrace", "staff", "needs a map").Ok);
        Assert.Equal(DraftStatus.Active, s.Draft("skyrace")!.Status);
        Assert.True(s.RequestPublic("pilot", "now with a map").Ok);
    }

    [Fact]
    public void live_arena_with_draft_reports_live()
    {
        var s = WithDraft();
        Assert.False(s.IsLiveWithDraft("skyrace"));
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        Assert.True(s.IsLiveWithDraft("skyrace"));
        Assert.False(s.IsLiveWithDraft("#skyrace"));
    }

    [Fact]
    public void publish_rate_limit_two_minutes()
    {
        var s = WithDraft();
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        Assert.True(s.PublishAllowed("skyrace"));
        Assert.True(s.Published("skyrace", "pilot").Ok);
        _now = _now.AddMinutes(1);
        Assert.False(s.PublishAllowed("skyrace"));
        _now = _now.AddMinutes(1);
        Assert.True(s.PublishAllowed("skyrace"));
        Assert.Contains(s.Events("skyrace", 10), e => e.Kind == "publish");
    }

    [Fact]
    public void archive_idle_drafts_after_30_days()
    {
        var s = WithDraft();
        _now = _now.AddDays(29);
        Assert.Empty(s.ArchiveIdleDrafts(TimeSpan.FromDays(30), _ => false));
        s.TouchDraft("skyrace");
        _now = _now.AddDays(31);
        Assert.Empty(s.ArchiveIdleDrafts(TimeSpan.FromDays(30), n => n == "#skyrace"));
        Assert.Equal(new[] { "#skyrace" }, s.ArchiveIdleDrafts(TimeSpan.FromDays(30), _ => false));
        Assert.Equal(DraftStatus.Archived, s.Draft("skyrace")!.Status);
        Assert.Null(s.RoleIn("#skyrace", "pilot"));
        Assert.Contains("#skyrace", s.PendingArchiveMoves());
        // the slot is free, the name is not
        Assert.False(s.CreateDraft("Other", "skyrace", NoFolder, Made).Ok);
        Assert.True(s.CreateDraft("pilot", "newone", NoFolder, Made).Ok);
    }

    [Fact]
    public void revive_draft_restores_status()
    {
        var s = WithDraft();
        _now = _now.AddDays(31);
        s.ArchiveIdleDrafts(TimeSpan.FromDays(30), _ => false);
        Assert.True(s.ReviveDraft("skyrace", "staff", Made).Ok);
        Assert.Equal(DraftStatus.Active, s.Draft("skyrace")!.Status);
        Assert.DoesNotContain("#skyrace", s.PendingArchiveMoves());
    }

    [Fact]
    public void backfill_creates_live_drafts_with_comods()
    {
        var s = Open();
        Assert.True(s.Request("Owner", "bigarena", "pitch").Ok);
        Assert.True(s.Approve("bigarena", "staff", NoFolder).Ok);
        // an approval made before drafts existed: drop the draft the new approve made
        ForgetDraft("bigarena");
        Assert.True(s.AddCoMod("bigarena", "owner", "Helper").Ok);
        var copied = new List<string>();
        var made = s.BackfillDrafts(n => { copied.Add(n); return null; });
        Assert.Equal(new[] { "bigarena" }, made);
        Assert.Equal(new[] { "bigarena" }, copied);
        var d = s.Draft("bigarena")!;
        Assert.True(d.Live);
        Assert.Equal(DraftStatus.Published, d.Status);
        Assert.Equal("comod", s.RoleIn("#bigarena", "helper"));
        Assert.Empty(s.BackfillDrafts(_ => null));
    }

    [Fact]
    public void transfer_moves_the_draft()
    {
        var s = WithDraft();
        Assert.True(s.Invite("skyrace", "pilot", "friend", HasAccount).Ok);
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        Assert.True(s.Transfer("skyrace", "staff", "Heir").Ok);
        Assert.Equal("owner", s.RoleIn("#skyrace", "heir"));
        Assert.Null(s.RoleIn("#skyrace", "pilot"));
        Assert.Null(s.RoleIn("skyrace", "pilot"));
        Assert.Empty(s.Draft("skyrace")!.Invited);
        Assert.Null(s.DraftOwnedBy("pilot"));
    }

    [Fact]
    public void roles_for_lists_draft_and_owned_arena()
    {
        var s = WithDraft();
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        Assert.Equal(new[] { ("#skyrace", "owner"), ("skyrace", "owner") }, s.RolesFor("pilot"));
    }

    [Fact]
    public void old_style_request_still_works_and_its_approval_creates_a_live_draft()
    {
        var s = Open();
        Assert.True(s.Request("Owner", "oldway", "pitch").Ok);
        Assert.False(s.IsDraftRequest("oldway"));
        Assert.True(s.Approve("oldway", "staff", NoFolder).Ok);
        var d = s.Draft("oldway")!;
        Assert.True(d.Live);
        Assert.Equal("owner", s.RoleIn("#oldway", "owner"));
    }

    [Fact]
    public void rollback_is_a_public_event()
    {
        var s = WithDraft();
        Assert.False(s.RolledBack("skyrace", "staff").Ok); // not public yet
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        Assert.True(s.RolledBack("skyrace", "staff").Ok);
        Assert.Contains(s.Events("skyrace", 10), e => e.Kind == "rollback" && e.Actor == "staff");
    }

    [Fact]
    public void owned_or_draft_reads_hash_names()
    {
        var s = WithDraft();
        Assert.True(s.AddCoMod("#skyrace", "pilot", "Helper").Ok);
        var a = s.OwnedOrDraft("#SkyRace");
        Assert.NotNull(a);
        Assert.Equal("#skyrace", a!.Name);
        Assert.Equal("Pilot", a.Owner);
        Assert.Equal(ArenaStatus.Active, a.Status);
        Assert.Equal(new[] { "Helper" }, a.CoMods);
        Assert.Null(s.OwnedOrDraft("#nothing"));
        Assert.Null(s.OwnedOrDraft("skyrace")); // not public yet
    }

    [Fact]
    public void live_drafts_are_never_idle_archived()
    {
        var s = WithDraft();
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        _now = _now.AddDays(90);
        Assert.Empty(s.ArchiveIdleDrafts(TimeSpan.FromDays(30), _ => false));
        Assert.Equal("owner", s.RoleIn("#skyrace", "pilot"));
    }

    [Fact]
    public void comods_of_a_live_arena_and_its_draft_stay_the_same()
    {
        var s = WithDraft();
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        Assert.True(s.AddCoMod("skyrace", "pilot", "Bob").Ok);
        Assert.Equal("comod", s.RoleIn("#skyrace", "bob"));
        Assert.True(s.RemoveCoMod("skyrace", "pilot", "bob").Ok);
        Assert.Null(s.RoleIn("#skyrace", "bob"));
        Assert.True(s.AddCoMod("#skyrace", "pilot", "Cat").Ok);
        Assert.Equal("comod", s.RoleIn("skyrace", "cat"));
        Assert.True(s.RemoveCoMod("#skyrace", "pilot", "cat").Ok);
        Assert.Null(s.RoleIn("skyrace", "cat"));
        // a draft that isn't public keeps its own list
        var t = Open();
        Assert.True(t.CreateDraft("Solo", "solo", NoFolder, Made).Ok);
        Assert.True(t.AddCoMod("#solo", "solo", "Dan").Ok);
        Assert.Null(t.RoleIn("solo", "dan"));
    }

    [Fact]
    public void roles_for_lists_the_draft_first_when_the_live_arena_publishes_from_it()
    {
        var s = WithDraft();
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Approve("skyrace", "staff", NoFolder).Ok);
        Assert.Equal(("#skyrace", "owner"), s.RolesFor("pilot")[0]);
    }

    [Fact]
    public void a_pilot_withdraws_their_pending_request()
    {
        var s = Open();
        Assert.True(s.Request("Pilot", "skyrace", "pitch").Ok);
        Assert.False(s.Withdraw("someone", "skyrace").Ok);
        Assert.True(s.Withdraw("PILOT", "skyrace").Ok);
        Assert.Empty(s.Requests(true, 10));
        Assert.Contains(s.Events("skyrace", 10), e => e.Kind == "withdraw");
        Assert.False(s.Withdraw("pilot", "skyrace").Ok); // nothing pending now
        Assert.True(s.CreateDraft("pilot", "skyrace", NoFolder, Made).Ok); // the name is free again
    }

    [Fact]
    public void withdrawing_a_drafts_request_returns_it_to_editing()
    {
        var s = WithDraft();
        Assert.True(s.RequestPublic("pilot", "p").Ok);
        Assert.True(s.Withdraw("pilot", "skyrace").Ok);
        Assert.Equal(DraftStatus.Active, s.Draft("skyrace")!.Status);
        Assert.True(s.RequestPublic("pilot", "again").Ok);
    }
}
