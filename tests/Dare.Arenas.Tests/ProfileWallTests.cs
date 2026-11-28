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

    private WallPost Post(Account who, string wall, string text, long? replyTo = null)
    {
        var r = _s.PostOnWall(who.Id, wall, text, out var p, replyTo);
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

    [Fact]
    public void owners_may_post_on_their_own_wall_without_notifying_themselves()
    {
        Post(_dare, "Big Dare", "welcome to my wall");
        Assert.Single(_s.WallPosts("Big Dare", false)!);
        Assert.Empty(_s.Notifications(_dare.Id));
    }

    [Fact]
    public void replies_hang_under_the_thread_and_notify_its_author_and_the_owner()
    {
        var top = Post(_kim, "Big Dare", "gg");
        var reply = Post(_lee, "Big Dare", "it was close", top.Id);
        var replyToReply = Post(_dare, "Big Dare", "rematch?", reply.Id); // goes under the same thread
        Assert.Equal(top.Id, replyToReply.ParentId);
        var wall = _s.WallPosts("Big Dare", false)!;
        Assert.Single(wall);
        Assert.Equal(["it was close", "rematch?"], wall[0].Replies.Select(r => r.Text));
        Assert.Equal(["reply", "reply"], _s.Notifications(_kim.Id).Select(n => n.Kind));
        Assert.Equal(["wall", "wall"], _s.Notifications(_dare.Id).Select(n => n.Kind)); // kim's post and lee's reply
        Assert.False(_s.PostOnWall(_kim.Id, "Kim", "wrong wall", out _, top.Id).Ok);
    }

    [Fact]
    public void mentions_find_names_with_spaces_and_notify_once()
    {
        var p = Post(_kim, "Lee", "@Big Dare and @lee, duel later? mail me at x@example.com @nobody");
        Assert.Equal(["Big Dare", "Lee"], p.Mentions);
        Assert.Equal(["mention"], _s.Notifications(_dare.Id).Select(n => n.Kind));
        Assert.Equal(["wall"], _s.Notifications(_lee.Id).Select(n => n.Kind)); // the owner hears once, not twice
        Assert.Equal(["Big Dare", "Lee"], _s.WallPosts("Lee", false)![0].Mentions);
    }

    [Fact]
    public void removing_a_message_drops_its_notifications()
    {
        var p = Post(_kim, "Big Dare", "hi @Lee");
        Assert.Single(_s.Notifications(_lee.Id));
        Assert.True(_s.RemoveWallPost(_kim.Id, p.Id, false).Ok);
        Assert.Empty(_s.Notifications(_lee.Id));
        Assert.Equal(0, _s.InboxCounts(_dare.Id).Notes);
    }

    [Fact]
    public void counts_and_reading_notifications()
    {
        Post(_kim, "Big Dare", "one");
        Post(_lee, "Big Dare", "two");
        var (notes, _, latest) = _s.InboxCounts(_dare.Id);
        Assert.Equal(2, notes);
        Assert.Equal(["two"], _s.Notifications(_dare.Id, 10, latest - 1).Select(n => n.Text));
        _s.MarkNotificationsRead(_dare.Id);
        Assert.Equal(0, _s.InboxCounts(_dare.Id).Notes);
        Assert.All(_s.Notifications(_dare.Id), n => Assert.True(n.Read));
    }

    [Fact]
    public void direct_messages_make_conversations_and_read_marks()
    {
        Assert.True(_s.SendMessage(_kim.Id, "big dare", "hey", out var m1).Ok);
        Assert.Equal("Big Dare", m1!.To);
        _now += TimeSpan.FromSeconds(5);
        Assert.True(_s.SendMessage(_dare.Id, "Kim", "hi back", out _).Ok);
        _now += TimeSpan.FromSeconds(5);
        Assert.True(_s.SendMessage(_lee.Id, "Big Dare", "yo", out _).Ok);
        Assert.Equal(2, _s.InboxCounts(_dare.Id).Messages); // kim's hey (still unread) and lee's yo
        var convs = _s.Conversations(_dare.Id);
        Assert.Equal(["Lee", "Kim"], convs.Select(c => c.With));
        Assert.Equal((1, false), (convs[0].Unread, convs[0].LastFromMe));
        Assert.True(convs[1].LastFromMe);
        var thread = _s.Thread(_dare.Id, "kim")!;
        Assert.Equal(["hey", "hi back"], thread.Select(m => m.Text));
        Assert.Equal(1, _s.InboxCounts(_dare.Id).Messages);
        Assert.Null(_s.Thread(_dare.Id, "Nobody Here"));
    }

    [Fact]
    public void direct_messages_refuse_self_unknown_empty_and_floods()
    {
        Assert.False(_s.SendMessage(_kim.Id, "Kim", "me", out _).Ok);
        Assert.False(_s.SendMessage(_kim.Id, "Nobody Here", "hi", out _).Ok);
        Assert.False(_s.SendMessage(_kim.Id, "Lee", "  ", out _).Ok);
        Assert.True(_s.SendMessage(_kim.Id, "Lee", "one", out _).Ok);
        Assert.False(_s.SendMessage(_kim.Id, "Lee", "too fast", out _).Ok);
    }

    [Fact]
    public void name_search_is_a_prefix_match()
    {
        Assert.Equal(["Big Dare"], _s.SearchNames("bi"));
        Assert.Equal(["Kim"], _s.SearchNames("K"));
        Assert.Empty(_s.SearchNames("%"));
        Assert.Empty(_s.SearchNames(""));
    }
}
