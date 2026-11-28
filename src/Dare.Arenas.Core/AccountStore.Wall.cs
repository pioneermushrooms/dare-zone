namespace Dare.Arenas.Core;

/// <summary>One message on a pilot's wall: who left it, what it says, when.</summary>
public sealed record WallPost(long Id, string Author, string Text, DateTimeOffset At);

/// <summary>
/// Profile walls (<c>/pilot/&lt;name&gt;</c>): signed-in pilots leave short, dated messages on another
/// pilot's profile. Plain text only. The wall's owner, the message's author and zone staff can remove a
/// message (it is kept, marked removed). A wall follows its profile: hidden by staff, it reads as unknown.
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxWallLength = 300;
    /// <summary>Slow enough to make flooding pointless: one message per author every 15 s, 30 an hour.</summary>
    public static readonly TimeSpan WallGap = TimeSpan.FromSeconds(15);
    public const int WallPerHour = 30;

    private void CreateWallTable() => Exec("""
        CREATE TABLE IF NOT EXISTS wall_posts (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          wall_id INTEGER NOT NULL,
          author_id INTEGER NOT NULL,
          text TEXT NOT NULL,
          created TEXT NOT NULL,
          removed INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS wall_posts_wall ON wall_posts(wall_id, id);
        CREATE INDEX IF NOT EXISTS wall_posts_author ON wall_posts(author_id, created);
        """);

    /// <summary>
    /// The newest messages on <paramref name="name"/>'s wall, newest first, older than <paramref name="before"/>
    /// when given. Null when there is no such profile to show (unknown, inactive, or hidden and not privileged).
    /// </summary>
    public IReadOnlyList<WallPost>? WallPosts(string name, bool privileged, long? before = null, int limit = 20)
    {
        lock (_gate)
        {
            if (PublicPilot(name, privileged) is null) return null;
            var wall = LoadAccount("name = $n", ("$n", (name ?? "").Trim()))!;
            using var c = Cmd("""
                SELECT w.id, a.name, w.text, w.created FROM wall_posts w JOIN accounts a ON a.id = w.author_id
                WHERE w.wall_id = $w AND w.removed = 0 AND w.id < $b
                ORDER BY w.id DESC LIMIT $l
                """, ("$w", wall.Id), ("$b", before ?? long.MaxValue), ("$l", Math.Clamp(limit, 1, 50)));
            using var r = c.ExecuteReader();
            var list = new List<WallPost>();
            while (r.Read()) list.Add(new WallPost(r.GetInt64(0), r.GetString(1), r.GetString(2), Parse(r.GetString(3))));
            return list;
        }
    }

    /// <summary>Leave a message on <paramref name="name"/>'s wall as <paramref name="authorId"/>. The new message comes back in <paramref name="post"/>.</summary>
    public Result PostOnWall(long authorId, string name, string? text, out WallPost? post)
    {
        WallPost? made = null;
        var clean = CleanBio(text);
        var res = Tx(() =>
        {
            if (clean.Length == 0) return Fail("Write something first.");
            if (clean.Length > MaxWallLength) return Fail($"Keep it to {MaxWallLength} characters.");
            var me = LoadAccount("id = $id", ("$id", authorId));
            if (me is null || me.Status != "active") return Fail("Sign in again first.");
            if (PublicPilot(name, privileged: false) is null) return Fail("No pilot by that name.");
            var wall = LoadAccount("name = $n", ("$n", (name ?? "").Trim()))!;
            var now = _clock();
            if (Scalar("SELECT COUNT(*) FROM wall_posts WHERE author_id = $a AND created > $t", ("$a", authorId), ("$t", Fmt(now - WallGap))) > 0)
                return Fail("One message every few seconds: try again in a moment.");
            if (Scalar("SELECT COUNT(*) FROM wall_posts WHERE author_id = $a AND created > $t", ("$a", authorId), ("$t", Fmt(now - TimeSpan.FromHours(1)))) >= WallPerHour)
                return Fail("That's a lot of messages for one hour. Try again later.");
            Run("INSERT INTO wall_posts(wall_id, author_id, text, created) VALUES($w, $a, $x, $c)",
                ("$w", wall.Id), ("$a", authorId), ("$x", clean), ("$c", Fmt(now)));
            made = new WallPost(Scalar("SELECT last_insert_rowid()"), me.Name, clean, Parse(Fmt(now)));
            return Ok("Posted.");
        });
        post = made;
        return res;
    }

    /// <summary>Remove a message: its author, the wall's owner, or staff (<paramref name="staff"/>) may.</summary>
    public Result RemoveWallPost(long actorId, long postId, bool staff) => Tx(() =>
    {
        using (var c = Cmd("SELECT wall_id, author_id FROM wall_posts WHERE id = $p AND removed = 0", ("$p", postId)))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) return Fail("That message is already gone.");
            if (!staff && r.GetInt64(0) != actorId && r.GetInt64(1) != actorId) return Fail("Only its author, the wall's owner or staff can remove it.");
        }
        Run("UPDATE wall_posts SET removed = 1 WHERE id = $p", ("$p", postId));
        return Ok("Removed.");
    });
}
