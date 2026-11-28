namespace Dare.Arenas.Core;

/// <summary>
/// Something that happened for a pilot: <see cref="Kind"/> is "wall" (a message on their wall), "reply" (a reply to
/// their message) or "mention" (an @mention). <see cref="Wall"/> is whose wall it is on; <see cref="Text"/> the message.
/// </summary>
public sealed record InboxNote(long Id, string Kind, string Actor, string Wall, long PostId, string Text, DateTimeOffset At, bool Read);

/// <summary>One direct message between two pilots.</summary>
public sealed record DirectMessage(long Id, string From, string To, string Text, DateTimeOffset At, bool Read);

/// <summary>A conversation in a pilot's inbox: the other pilot, the last message, and how many are unread.</summary>
public sealed record Conversation(string With, string LastText, DateTimeOffset LastAt, bool LastFromMe, int Unread);

/// <summary>
/// The site inbox: notifications (wall messages, replies, @mentions) and direct messages between pilots. Plain text;
/// the author and recipient are pilot names (accounts). Direct messages are slowed like wall messages.
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxMessageLength = 1000;
    public static readonly TimeSpan MessageGap = TimeSpan.FromSeconds(2);
    public const int MessagesPerHour = 120;

    private void CreateInboxTables() => Exec("""
        CREATE TABLE IF NOT EXISTS notifications (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          account_id INTEGER NOT NULL,
          kind TEXT NOT NULL,
          actor_id INTEGER NOT NULL,
          wall_id INTEGER NOT NULL,
          post_id INTEGER NOT NULL,
          created TEXT NOT NULL,
          read INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS notifications_account ON notifications(account_id, id);
        CREATE TABLE IF NOT EXISTS direct_messages (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          from_id INTEGER NOT NULL,
          to_id INTEGER NOT NULL,
          text TEXT NOT NULL,
          created TEXT NOT NULL,
          read INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS direct_messages_to ON direct_messages(to_id, read);
        CREATE INDEX IF NOT EXISTS direct_messages_pair ON direct_messages(from_id, to_id, id);
        """);

    /// <summary>Under the lock, in a transaction.</summary>
    private void Notify(long accountId, string kind, long actorId, long wallId, long postId) =>
        Run("INSERT INTO notifications(account_id, kind, actor_id, wall_id, post_id, created) VALUES($a, $k, $x, $w, $p, $c)",
            ("$a", accountId), ("$k", kind), ("$x", actorId), ("$w", wallId), ("$p", postId), ("$c", Now()));

    /// <summary>How many notifications and direct messages <paramref name="accountId"/> hasn't read, and the newest notification's id.</summary>
    public (int Notes, int Messages, long LatestNote) InboxCounts(long accountId)
    {
        lock (_gate)
        {
            var notes = (int)Scalar("""
                SELECT COUNT(*) FROM notifications n JOIN wall_posts w ON w.id = n.post_id
                WHERE n.account_id = $a AND n.read = 0 AND w.removed = 0
                """, ("$a", accountId));
            var msgs = (int)Scalar("SELECT COUNT(*) FROM direct_messages WHERE to_id = $a AND read = 0", ("$a", accountId));
            var latest = Scalar("SELECT COALESCE(MAX(id), 0) FROM notifications WHERE account_id = $a", ("$a", accountId));
            return (notes, msgs, latest);
        }
    }

    /// <summary>The newest notifications for <paramref name="accountId"/> (newer than <paramref name="after"/> when given), newest first.</summary>
    public IReadOnlyList<InboxNote> Notifications(long accountId, int limit = 30, long after = 0)
    {
        lock (_gate)
        {
            using var c = Cmd("""
                SELECT n.id, n.kind, x.name, o.name, n.post_id, w.text, n.created, n.read
                FROM notifications n
                JOIN accounts x ON x.id = n.actor_id
                JOIN accounts o ON o.id = n.wall_id
                JOIN wall_posts w ON w.id = n.post_id
                WHERE n.account_id = $a AND n.id > $after AND w.removed = 0
                ORDER BY n.id DESC LIMIT $l
                """, ("$a", accountId), ("$after", after), ("$l", Math.Clamp(limit, 1, 100)));
            using var r = c.ExecuteReader();
            var list = new List<InboxNote>();
            while (r.Read())
                list.Add(new InboxNote(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                    Parse(r.GetString(6)), r.GetInt64(7) != 0));
            return list;
        }
    }

    /// <summary>Marks every notification of <paramref name="accountId"/> read.</summary>
    public void MarkNotificationsRead(long accountId)
    {
        lock (_gate) Run("UPDATE notifications SET read = 1 WHERE account_id = $a AND read = 0", ("$a", accountId));
    }

    /// <summary>Send <paramref name="text"/> from <paramref name="fromId"/> to the pilot named <paramref name="to"/>.</summary>
    public Result SendMessage(long fromId, string to, string? text, out DirectMessage? sent)
    {
        DirectMessage? made = null;
        var clean = CleanBio(text);
        var res = Tx(() =>
        {
            if (clean.Length == 0) return Fail("Write something first.");
            if (clean.Length > MaxMessageLength) return Fail($"Keep it to {MaxMessageLength} characters.");
            var me = LoadAccount("id = $id", ("$id", fromId));
            if (me is null || me.Status != "active") return Fail("Sign in again first.");
            var them = LoadAccount("name = $n", ("$n", (to ?? "").Trim()));
            if (them is null || them.Status != "active") return Fail("No pilot by that name.");
            if (them.Id == me.Id) return Fail("That's you.");
            var now = _clock();
            if (Scalar("SELECT COUNT(*) FROM direct_messages WHERE from_id = $a AND created > $t", ("$a", fromId), ("$t", Fmt(now - MessageGap))) > 0)
                return Fail("Slow down a little.");
            if (Scalar("SELECT COUNT(*) FROM direct_messages WHERE from_id = $a AND created > $t", ("$a", fromId), ("$t", Fmt(now - TimeSpan.FromHours(1)))) >= MessagesPerHour)
                return Fail("That's a lot of messages for one hour. Try again later.");
            Run("INSERT INTO direct_messages(from_id, to_id, text, created) VALUES($f, $t, $x, $c)",
                ("$f", me.Id), ("$t", them.Id), ("$x", clean), ("$c", Fmt(now)));
            made = new DirectMessage(Scalar("SELECT last_insert_rowid()"), me.Name, them.Name, clean, Parse(Fmt(now)), false);
            return Ok("Sent.");
        });
        sent = made;
        return res;
    }

    /// <summary><paramref name="accountId"/>'s conversations, the most recent first.</summary>
    public IReadOnlyList<Conversation> Conversations(long accountId, int limit = 50)
    {
        lock (_gate)
        {
            using var c = Cmd("""
                WITH mine AS (
                  SELECT id, CASE WHEN from_id = $a THEN to_id ELSE from_id END AS other, from_id = $a AS fromme, text, created,
                         CASE WHEN to_id = $a AND read = 0 THEN 1 ELSE 0 END AS unread
                  FROM direct_messages WHERE from_id = $a OR to_id = $a),
                last AS (SELECT other, MAX(id) AS lastid, SUM(unread) AS unread FROM mine GROUP BY other)
                SELECT a.name, m.text, m.created, m.fromme, l.unread
                FROM last l JOIN mine m ON m.id = l.lastid JOIN accounts a ON a.id = l.other
                ORDER BY l.lastid DESC LIMIT $l
                """, ("$a", accountId), ("$l", Math.Clamp(limit, 1, 200)));
            using var r = c.ExecuteReader();
            var list = new List<Conversation>();
            while (r.Read())
                list.Add(new Conversation(r.GetString(0), r.GetString(1), Parse(r.GetString(2)), r.GetInt64(3) != 0, (int)r.GetInt64(4)));
            return list;
        }
    }

    /// <summary>
    /// The messages between <paramref name="accountId"/> and <paramref name="with"/>, oldest first (the newest
    /// <paramref name="limit"/>, older than <paramref name="before"/> when given); the ones they received are marked read.
    /// Null for an unknown pilot.
    /// </summary>
    public IReadOnlyList<DirectMessage>? Thread(long accountId, string with, long? before = null, int limit = 50)
    {
        lock (_gate)
        {
            var them = LoadAccount("name = $n", ("$n", (with ?? "").Trim()));
            if (them is null) return null;
            using var c = Cmd("""
                SELECT d.id, f.name, t.name, d.text, d.created, d.read FROM direct_messages d
                JOIN accounts f ON f.id = d.from_id JOIN accounts t ON t.id = d.to_id
                WHERE ((d.from_id = $a AND d.to_id = $b) OR (d.from_id = $b AND d.to_id = $a)) AND d.id < $before
                ORDER BY d.id DESC LIMIT $l
                """, ("$a", accountId), ("$b", them.Id), ("$before", before ?? long.MaxValue), ("$l", Math.Clamp(limit, 1, 200)));
            var list = new List<DirectMessage>();
            using (var r = c.ExecuteReader())
                while (r.Read())
                    list.Add(new DirectMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4)), r.GetInt64(5) != 0));
            Run("UPDATE direct_messages SET read = 1 WHERE from_id = $b AND to_id = $a AND read = 0", ("$a", accountId), ("$b", them.Id));
            list.Reverse();
            return list;
        }
    }

    /// <summary>Active pilot names starting with <paramref name="prefix"/> (for @mentions and the To field), alphabetical.</summary>
    public IReadOnlyList<string> SearchNames(string prefix, int limit = 8)
    {
        var p = (prefix ?? "").Trim();
        if (p.Length == 0) return [];
        var like = p.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        lock (_gate)
        {
            using var c = Cmd("SELECT name FROM accounts WHERE status = 'active' AND name LIKE $p ESCAPE '\\' ORDER BY name COLLATE NOCASE LIMIT $l",
                ("$p", like), ("$l", Math.Clamp(limit, 1, 20)));
            using var r = c.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }
}
