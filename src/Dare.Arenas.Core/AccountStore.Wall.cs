namespace Dare.Arenas.Core;

/// <summary>
/// One message on a pilot's wall: who left it, what it says, when, the pilots it @mentions, and (for a message
/// that starts a thread) its replies, oldest first. A reply has <see cref="ParentId"/> and no replies of its own.
/// </summary>
public sealed record WallPost(long Id, string Author, string Text, DateTimeOffset At, long? ParentId,
    IReadOnlyList<string> Mentions, IReadOnlyList<WallPost> Replies);

/// <summary>
/// Profile walls (<c>/pilot/&lt;name&gt;</c>): signed-in pilots leave short, dated messages on a profile (their own
/// too), reply to them (one level), and @mention other pilots. Plain text only. The wall's owner, the message's
/// author and zone staff can remove a message (it is kept, marked removed; removing one that starts a thread
/// hides its replies). A wall follows its profile: hidden by staff, it reads as unknown. Posting notifies the
/// wall's owner, the author of the message replied to, and everyone mentioned (AccountStore.Inbox.cs).
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxWallLength = 300;
    /// <summary>Slow enough to make flooding pointless: one message per author every 15 s, 30 an hour.</summary>
    public static readonly TimeSpan WallGap = TimeSpan.FromSeconds(15);
    public const int WallPerHour = 30;
    /// <summary>At most this many pilots are notified from one message's @mentions.</summary>
    public const int MaxMentions = 5;

    private void CreateWallTable()
    {
        Exec("""
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
        // replies and mentions came later
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('wall_posts') WHERE name = 'parent_id'") == 0)
            Exec("ALTER TABLE wall_posts ADD COLUMN parent_id INTEGER NULL;");
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('wall_posts') WHERE name = 'mentions'") == 0)
            Exec("ALTER TABLE wall_posts ADD COLUMN mentions TEXT NOT NULL DEFAULT '';");
        Exec("CREATE INDEX IF NOT EXISTS wall_posts_parent ON wall_posts(parent_id);");
    }

    /// <summary>
    /// The newest threads on <paramref name="name"/>'s wall, newest first, older than <paramref name="before"/> when
    /// given, each with its replies. Null when there is no such profile to show (unknown, inactive, or hidden and not
    /// privileged).
    /// </summary>
    public IReadOnlyList<WallPost>? WallPosts(string name, bool privileged, long? before = null, int limit = 20)
    {
        lock (_gate)
        {
            if (PublicPilot(name, privileged) is null) return null;
            var wall = LoadAccount("name = $n", ("$n", (name ?? "").Trim()))!;
            var tops = ReadPosts("""
                SELECT w.id, a.name, w.text, w.created, w.parent_id, w.mentions FROM wall_posts w JOIN accounts a ON a.id = w.author_id
                WHERE w.wall_id = $w AND w.removed = 0 AND w.parent_id IS NULL AND w.id < $b
                ORDER BY w.id DESC LIMIT $l
                """, ("$w", wall.Id), ("$b", before ?? long.MaxValue), ("$l", Math.Clamp(limit, 1, 50)));
            if (tops.Count == 0) return tops;
            var ids = string.Join(",", tops.Select(t => t.Id));
            var replies = ReadPosts($"""
                SELECT w.id, a.name, w.text, w.created, w.parent_id, w.mentions FROM wall_posts w JOIN accounts a ON a.id = w.author_id
                WHERE w.removed = 0 AND w.parent_id IN ({ids}) ORDER BY w.id
                """).ToLookup(r => r.ParentId);
            return tops.Select(t => t with { Replies = replies[t.Id].ToList() }).ToList();
        }
    }

    /// <summary>Under the lock.</summary>
    private List<WallPost> ReadPosts(string sql, params (string, object)[] ps)
    {
        using var c = Cmd(sql, ps);
        using var r = c.ExecuteReader();
        var list = new List<WallPost>();
        while (r.Read())
            list.Add(new WallPost(r.GetInt64(0), r.GetString(1), r.GetString(2), Parse(r.GetString(3)),
                r.IsDBNull(4) ? null : r.GetInt64(4), SplitNames(r.GetString(5)), []));
        return list;
    }

    private static IReadOnlyList<string> SplitNames(string s) => s.Length == 0 ? [] : s.Split('\t');

    /// <summary>
    /// Leave a message on <paramref name="name"/>'s wall as <paramref name="authorId"/>, or a reply to message
    /// <paramref name="replyTo"/> on it. The new message comes back in <paramref name="post"/>.
    /// </summary>
    public Result PostOnWall(long authorId, string name, string? text, out WallPost? post, long? replyTo = null)
    {
        WallPost? made = null;
        var clean = CleanBio(text);
        var res = Tx(() =>
        {
            if (clean.Length == 0) return Fail("Write something first.");
            if (clean.Length > MaxWallLength) return Fail($"Keep it to {MaxWallLength} characters.");
            var me = LoadAccount("id = $id", ("$id", authorId));
            if (me is null || me.Status != "active") return Fail("Sign in again first.");
            var mine = string.Equals(me.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (PublicPilot(name ?? "", privileged: mine) is null) return Fail("No pilot by that name.");
            var wall = LoadAccount("name = $n", ("$n", (name ?? "").Trim()))!;
            // a reply goes under the message that starts its thread
            long? parent = null, parentAuthor = null, threadAuthor = null;
            if (replyTo is { } rt)
            {
                using var c = Cmd("SELECT id, parent_id, author_id FROM wall_posts WHERE id = $p AND wall_id = $w AND removed = 0", ("$p", rt), ("$w", wall.Id));
                using var r = c.ExecuteReader();
                if (!r.Read()) return Fail("That message is gone.");
                parent = r.IsDBNull(1) ? r.GetInt64(0) : r.GetInt64(1);
                parentAuthor = r.GetInt64(2);
            }
            if (parent is { } top && top != replyTo)
                threadAuthor = Scalar("SELECT author_id FROM wall_posts WHERE id = $p", ("$p", top));
            var now = _clock();
            if (Scalar("SELECT COUNT(*) FROM wall_posts WHERE author_id = $a AND created > $t", ("$a", authorId), ("$t", Fmt(now - WallGap))) > 0)
                return Fail("One message every few seconds: try again in a moment.");
            if (Scalar("SELECT COUNT(*) FROM wall_posts WHERE author_id = $a AND created > $t", ("$a", authorId), ("$t", Fmt(now - TimeSpan.FromHours(1)))) >= WallPerHour)
                return Fail("That's a lot of messages for one hour. Try again later.");
            var mentioned = FindMentions(clean);
            Run("INSERT INTO wall_posts(wall_id, author_id, text, created, parent_id, mentions) VALUES($w, $a, $x, $c, $p, $m)",
                ("$w", wall.Id), ("$a", authorId), ("$x", clean), ("$c", Fmt(now)), ("$p", parent is { } pp ? pp : DBNull.Value),
                ("$m", string.Join('\t', mentioned.Select(m => m.Name))));
            var id = Scalar("SELECT last_insert_rowid()");
            // who hears about it: whoever wrote the message replied to and whoever started its thread, the wall's owner,
            // and everyone mentioned (once each, never the author)
            var told = new HashSet<long> { authorId };
            if (parentAuthor is { } pa && told.Add(pa)) Notify(pa, "reply", authorId, wall.Id, id);
            if (threadAuthor is { } ta && told.Add(ta)) Notify(ta, "reply", authorId, wall.Id, id);
            if (told.Add(wall.Id)) Notify(wall.Id, "wall", authorId, wall.Id, id);
            foreach (var m in mentioned) if (told.Add(m.Id)) Notify(m.Id, "mention", authorId, wall.Id, id);
            made = new WallPost(id, me.Name, clean, Parse(Fmt(now)), parent, mentioned.Select(m => m.Name).ToList(), []);
            return Ok("Posted.");
        });
        post = made;
        return res;
    }

    /// <summary>
    /// The pilots a message @mentions, by their stored names: after each @ (not inside a word), the longest run of
    /// text that is an active pilot's name and ends at a word boundary ("@Big Dare hi" finds Big Dare). At most
    /// <see cref="MaxMentions"/>. Under the lock.
    /// </summary>
    private List<(long Id, string Name)> FindMentions(string text)
    {
        var found = new List<(long, string)>();
        for (var i = text.IndexOf('@'); i >= 0 && found.Count < MaxMentions; i = text.IndexOf('@', i + 1))
        {
            if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue; // an email address, not a mention
            var rest = text[(i + 1)..];
            for (var len = Math.Min(rest.Length, 24); len > 0; len--)
            {
                var cand = rest[..len];
                if (char.IsWhiteSpace(cand[^1]) || (len < rest.Length && char.IsLetterOrDigit(rest[len]))) continue;
                using var c = Cmd("SELECT id, name FROM accounts WHERE name = $n AND status = 'active'", ("$n", cand));
                using var r = c.ExecuteReader();
                if (!r.Read()) continue;
                var who = (r.GetInt64(0), r.GetString(1));
                if (!found.Contains(who)) found.Add(who);
                break;
            }
        }
        return found;
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
        Run("DELETE FROM notifications WHERE post_id = $p", ("$p", postId));
        return Ok("Removed.");
    });
}
