using System.Buffers.Binary;
using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Public pilot profiles: per name, owner settings, staff moderation, nothing private in the public view.
public sealed class PilotProfileTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-profile-{Guid.NewGuid():N}.db");
    private readonly AccountStore _s;
    private readonly Account _dare;

    public PilotProfileTests()
    {
        _s = new AccountStore(_db, () => new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero));
        Assert.True(_s.StartSignup("Big Dare", "hunter22pw", "dare@example.com", out var code).Ok);
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

    private static byte[] Sheet()
    {
        var b = new byte[33 + 64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 360);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 144);
        return b;
    }

    private long Ship(long accountId, string name = "Hawk")
    {
        Assert.True(_s.AddShipyardShip(accountId, name, "generated", Sheet(), out var id).Ok);
        return id;
    }

    [Fact]
    public void defaults_show_everything_and_no_bio()
    {
        var p = _s.ProfileSettings(_dare.Id);
        Assert.Equal(new PilotProfileSettings("", null, true, true, true, false), p);
        var pub = _s.PublicPilot("big dare", privileged: false)!;
        Assert.Equal("Big Dare", pub.Name); // stored casing
        Assert.Equal(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero), pub.Since);
        Assert.Equal("", pub.Bio);
        Assert.Null(pub.Avatar);
        Assert.False(pub.Hidden);
    }

    [Fact]
    public void unknown_name_has_no_profile()
    {
        Assert.Null(_s.PublicPilot("nobody", privileged: true));
        Assert.Null(_s.PublicPilot("", privileged: false));
    }

    [Fact]
    public void owner_saves_bio_avatar_and_switches()
    {
        var hawk = Ship(_dare.Id);
        var r = _s.SaveProfile(_dare.Id, "  I fly <b>warbirds</b>.\u0007  ", hawk, showContribs: false, showArenas: true, showShips: false);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new PilotProfileSettings("I fly <b>warbirds</b>.", hawk, false, true, false, false), _s.ProfileSettings(_dare.Id));
        var pub = _s.PublicPilot("Big Dare", false)!;
        Assert.Equal("I fly <b>warbirds</b>.", pub.Bio);
        Assert.Equal(hawk, pub.Avatar);
        Assert.False(pub.ShowContribs);
        Assert.False(pub.ShowShips);
        Assert.Empty(pub.Ships); // hidden
    }

    [Fact]
    public void bio_over_280_is_refused()
    {
        Assert.True(_s.SaveProfile(_dare.Id, new string('a', 280), null, true, true, true).Ok);
        var r = _s.SaveProfile(_dare.Id, new string('a', 281), null, true, true, true);
        Assert.False(r.Ok);
        Assert.Equal(280, _s.ProfileSettings(_dare.Id).Bio.Length);
    }

    [Fact]
    public void avatar_must_be_one_of_the_accounts_ships()
    {
        Assert.True(_s.AddPilot(_dare.Id, "Other Me", out var other).Ok);
        Assert.True(_s.SaveProfile(_dare.Id, "", Ship(other!.Id), true, true, true).Ok); // the account's, made by another name
        Assert.True(_s.CreateWithoutEmail("stranger", "hunter22pw", out var stranger).Ok);
        var theirs = Ship(stranger!.Id);
        Assert.False(_s.SaveProfile(_dare.Id, "", theirs, true, true, true).Ok);
        Assert.False(_s.SaveProfile(_dare.Id, "", 9999, true, true, true).Ok);
        Assert.NotNull(_s.ProfileSettings(_dare.Id).AvatarShip);
    }

    [Fact]
    public void public_view_is_per_name_and_names_no_siblings()
    {
        Assert.True(_s.AddPilot(_dare.Id, "Second Name", out var second).Ok);
        Assert.True(_s.SaveProfile(second!.Id, "second bio", null, true, true, true).Ok);
        var pub = _s.PublicPilot("Big Dare", false)!;
        Assert.Equal("", pub.Bio); // each name stands alone
        var flat = System.Text.Json.JsonSerializer.Serialize(pub);
        Assert.DoesNotContain("Second Name", flat);
        Assert.DoesNotContain("example.com", flat);
        Assert.DoesNotContain("Email", flat);
        Assert.DoesNotContain("LastLogin", flat);
        Assert.DoesNotContain("AccountId", flat);
    }

    [Fact]
    public void ships_are_the_slotted_ones_only()
    {
        var a = Ship(_dare.Id, "Alpha");
        Ship(_dare.Id, "Unused");
        Assert.True(_s.SetShipyardSlot(_dare.Id, 3, a).Ok);
        var pub = _s.PublicPilot("Big Dare", false)!;
        Assert.Equal(["Alpha"], pub.Ships.Select(x => x.Name));
    }

    [Fact]
    public void ship_sheet_only_for_slotted_or_avatar_ships()
    {
        var slotted = Ship(_dare.Id, "Slotted");
        var avatar = Ship(_dare.Id, "Avatar");
        var unused = Ship(_dare.Id, "Unused");
        Assert.True(_s.SetShipyardSlot(_dare.Id, 1, slotted).Ok);
        Assert.True(_s.SaveProfile(_dare.Id, "", avatar, true, true, showShips: true).Ok);
        Assert.NotNull(_s.ProfileShipSheet("big dare", slotted));
        Assert.NotNull(_s.ProfileShipSheet("big dare", avatar));
        Assert.Null(_s.ProfileShipSheet("big dare", unused));
        Assert.Null(_s.ProfileShipSheet("nobody", slotted));

        Assert.True(_s.SaveProfile(_dare.Id, "", avatar, true, true, showShips: false).Ok);
        Assert.Null(_s.ProfileShipSheet("big dare", slotted));
        Assert.NotNull(_s.ProfileShipSheet("big dare", avatar)); // avatar always shows
    }

    [Fact]
    public void removed_avatar_ship_drops_from_view()
    {
        var hawk = Ship(_dare.Id);
        Assert.True(_s.SaveProfile(_dare.Id, "", hawk, true, true, true).Ok);
        Assert.True(_s.RemoveShipyardShip(_dare.Id, hawk).Ok);
        Assert.Null(_s.PublicPilot("Big Dare", false)!.Avatar);
    }

    [Fact]
    public void staff_hide_and_clear_bio()
    {
        Assert.True(_s.SaveProfile(_dare.Id, "rude words", null, true, true, true).Ok);
        Assert.True(_s.ModerateProfile("big dare", clearBio: true, hidden: true).Ok);
        Assert.Null(_s.PublicPilot("Big Dare", privileged: false));
        var seen = _s.PublicPilot("Big Dare", privileged: true)!;
        Assert.True(seen.Hidden);
        Assert.Equal("", seen.Bio);
        Assert.Null(_s.ProfileShipSheet("Big Dare", 1));

        // the owner's own saves don't unhide
        Assert.True(_s.SaveProfile(_dare.Id, "nicer", null, true, true, true).Ok);
        Assert.Null(_s.PublicPilot("Big Dare", privileged: false));

        Assert.True(_s.ModerateProfile("Big Dare", clearBio: null, hidden: false).Ok);
        Assert.Equal("nicer", _s.PublicPilot("Big Dare", false)!.Bio);
        Assert.False(_s.ModerateProfile("nobody", true, null).Ok);
    }

    [Fact]
    public void deleting_a_name_drops_its_profile()
    {
        Assert.True(_s.AddPilot(_dare.Id, "Gone Soon", out var gone).Ok);
        Assert.True(_s.SaveProfile(gone!.Id, "old bio", null, false, false, false).Ok);
        Assert.True(_s.DeletePilot(_dare.Id, "Gone Soon", out _).Ok);
        Assert.Null(_s.PublicPilot("Gone Soon", true));
        Assert.Equal(new PilotProfileSettings("", null, true, true, true, false), _s.ProfileSettings(gone.Id));
    }
}
