using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// Profile walls: dated messages pilots leave on each other's profiles; who may remove them; flood limits;
// a wall follows its profile when staff hide it.
public sealed class ProfileWallTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-wall-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountStore _s;
    private readonly Account _dare, _kim, _lee;

    public ProfileWallTests()
    {
        _s = new AccountStore(_db, () => _now);
        _dare = Make("Big Dare", "dare@example.com");
        _kim = Make("Kim", "kim@example.com");
        _lee = Make("Lee", "lee@example.com");
    }

    private Account Make(string name, string email)
    {
        Assert.True(_s.StartSignup(name, "hunter22pw", email, out var code).Ok);
        Assert.True(_s.VerifySignup(email, code, out var a).Ok);
        return a!;
    }

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    private WallPost Post(Account who, string wall, string text)
    {
        var r = _s.PostOnWall(who.Id, wall, text, out var p);
        Assert.True(r.Ok, r.Message);
        _now += TimeSpan.FromSeconds(20);
        return p!;
    }

    [Fact]
    public void posts_come_back_newest_first_with_author_and_date()
    {
        var first = Post(_kim, "big dare", "  gg last night  ");
        Post(_lee, "Big Dare", "see you in duel");
        var wall = _s.WallPosts("Big Dare", privileged: false)!;
        Assert.Equal(["see you in duel", "gg last night"], wall.Select(p => p.Text));
        Assert.Equal(["Lee", "Kim"], wall.Select(p => p.Author));
        Assert.Equal(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero), first.At);
        Assert.Empty(_s.WallPosts("Kim", privileged: false)!);
    }

    [Fact]
    public void pages_with_before()
    {
        for (var i = 0; i < 5; i++) Post(_kim, "Big Dare", $"m{i}");
        var page1 = _s.WallPosts("Big Dare", false, null, 2)!;
        Assert.Equal(["m4", "m3"], page1.Select(p => p.Text));
        var page2 = _s.WallPosts("Big Dare", false, page1[^1].Id, 2)!;
        Assert.Equal(["m2", "m1"], page2.Select(p => p.Text));
    }

    [Fact]
    public void empty_long_and_unknown_are_refused()
    {
        Assert.False(_s.PostOnWall(_kim.Id, "Big Dare", "   ", out _).Ok);
        Assert.False(_s.PostOnWall(_kim.Id, "Big Dare", new string('x', AccountStore.MaxWallLength + 1), out _).Ok);
        Assert.False(_s.PostOnWall(_kim.Id, "Nobody Here", "hi", out _).Ok);
        Assert.Null(_s.WallPosts("Nobody Here", privileged: false));
    }

    [Fact]
    public void flooding_is_slowed()
    {
        Assert.True(_s.PostOnWall(_kim.Id, "Big Dare", "one", out _).Ok);
        Assert.False(_s.PostOnWall(_kim.Id, "Lee", "two, too soon", out _).Ok);
        _now += AccountStore.WallGap + TimeSpan.FromSeconds(1);
        Assert.True(_s.PostOnWall(_kim.Id, "Lee", "two", out _).Ok);
    }

    [Fact]
    public void hourly_cap()
    {
        for (var i = 0; i < AccountStore.WallPerHour; i++) Post(_kim, "Big Dare", $"m{i}");
        Assert.False(_s.PostOnWall(_kim.Id, "Big Dare", "one more", out _).Ok);
        _now += TimeSpan.FromHours(1);
        Assert.True(_s.PostOnWall(_kim.Id, "Big Dare", "next hour", out _).Ok);
    }

    [Fact]
    public void author_owner_and_staff_may_remove_others_may_not()
    {
        var a = Post(_kim, "Big Dare", "by kim");
        var b = Post(_kim, "Big Dare", "also by kim");
        var c = Post(_kim, "Big Dare", "third");
        Assert.False(_s.RemoveWallPost(_lee.Id, a.Id, staff: false).Ok);
        Assert.True(_s.RemoveWallPost(_kim.Id, a.Id, staff: false).Ok);   // its author
        Assert.True(_s.RemoveWallPost(_dare.Id, b.Id, staff: false).Ok);  // the wall's owner
        Assert.True(_s.RemoveWallPost(_lee.Id, c.Id, staff: true).Ok);    // staff
        Assert.False(_s.RemoveWallPost(_kim.Id, a.Id, staff: false).Ok);  // already gone
        Assert.Empty(_s.WallPosts("Big Dare", privileged: false)!);
    }

    [Fact]
    public void a_hidden_profile_hides_its_wall_and_takes_no_messages()
    {
        Post(_kim, "Big Dare", "hello");
        Assert.True(_s.ModerateProfile("Big Dare", null, hidden: true).Ok);
        Assert.Null(_s.WallPosts("Big Dare", privileged: false));
        Assert.Single(_s.WallPosts("Big Dare", privileged: true)!);
        Assert.False(_s.PostOnWall(_lee.Id, "Big Dare", "anyone?", out _).Ok);
    }
}
