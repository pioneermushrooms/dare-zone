namespace Dare.Arenas.Core;

/// <summary>One person's tutorial record. <c>Outcome</c> is existing, finished or left; null while it is under way.</summary>
public sealed record TutorialRecord(DateTimeOffset? FirstGameLogin, string? Outcome, int? Step, int? Act, int Dropouts);

/// <summary>
/// The in-game tutorial's record of who is new. It belongs to the person, not the pilot name: a
/// person who adds another name is not new. The public methods take any of the person's names.
/// </summary>
public sealed partial class AccountStore
{
    private const string TutorialMarker = "tutorial_backfill";

    /// <summary>
    /// Creates <c>tutorial_pilots</c> and, once (marker <c>tutorial_backfill</c>), gives every existing
    /// person outcome <c>existing</c>, so only people who sign up afterwards are new. After
    /// <see cref="EnsurePeople"/>, which gives every name its person.
    /// </summary>
    private void CreateTutorialTable()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS tutorial_pilots (
              person_id INTEGER PRIMARY KEY,
              first_game_login TEXT NULL,
              outcome TEXT NULL,
              step INTEGER NULL,
              act INTEGER NULL,
              dropouts INTEGER NOT NULL DEFAULT 0
            );
            """);
        if (Scalar("SELECT COUNT(*) FROM account_meta WHERE key = $k", ("$k", TutorialMarker)) > 0) return;
        Tx(() =>
        {
            Run("INSERT OR IGNORE INTO tutorial_pilots(person_id, outcome) SELECT id, 'existing' FROM people");
            Run("INSERT INTO account_meta(key, value) VALUES($k,$v)", ("$k", TutorialMarker), ("$v", Now()));
            return Ok("");
        });
    }

    /// <summary>The record of the person behind <paramref name="name"/>; null when they have none (new) or the name is unknown.</summary>
    public TutorialRecord? TutorialOf(string name)
    {
        lock (_gate)
        {
            if (PersonOfName(name) is not { } person) return null;
            using var c = Cmd("SELECT first_game_login, outcome, step, act, dropouts FROM tutorial_pilots WHERE person_id = $p", ("$p", person));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new TutorialRecord(
                Str(r, 0) is { } t ? Parse(t) : null,
                Str(r, 1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetInt32(3),
                r.GetInt32(4));
        }
    }

    /// <summary>A game login: makes the person's row (first login = now) when they have none. The caller checks <see cref="TutorialOf"/> first.</summary>
    public void NoteGameLogin(string name)
    {
        lock (_gate)
        {
            if (PersonOfName(name) is not { } person) return;
            Run("INSERT OR IGNORE INTO tutorial_pilots(person_id, first_game_login) VALUES($p, $t)", ("$p", person), ("$t", Now()));
        }
    }

    public void SetTutorialProgress(string name, int step, int act)
    {
        lock (_gate)
        {
            if (PersonOfName(name) is not { } person) return;
            Run("UPDATE tutorial_pilots SET step = $s, act = $a WHERE person_id = $p", ("$s", step), ("$a", act), ("$p", person));
        }
    }

    /// <param name="outcome">finished or left.</param>
    public void SetTutorialOutcome(string name, string outcome)
    {
        lock (_gate)
        {
            if (PersonOfName(name) is not { } person) return;
            Run("UPDATE tutorial_pilots SET outcome = $o WHERE person_id = $p", ("$o", outcome), ("$p", person));
        }
    }

    /// <summary>Counts one more drop-out; returns the new count (0 when the person has no row).</summary>
    public int AddTutorialDropout(string name)
    {
        lock (_gate)
        {
            if (PersonOfName(name) is not { } person) return 0;
            Run("UPDATE tutorial_pilots SET dropouts = dropouts + 1 WHERE person_id = $p", ("$p", person));
            using var c = Cmd("SELECT dropouts FROM tutorial_pilots WHERE person_id = $p", ("$p", person));
            return c.ExecuteScalar() is long n ? (int)n : 0;
        }
    }

    /// <summary>How many finished, how many left, and the step the ones who left stopped at.</summary>
    public (int Finished, int Left, IReadOnlyDictionary<int, int> StoppedAtStep) TutorialCounts()
    {
        lock (_gate)
        {
            var finished = (int)Scalar("SELECT COUNT(*) FROM tutorial_pilots WHERE outcome = 'finished'");
            var left = (int)Scalar("SELECT COUNT(*) FROM tutorial_pilots WHERE outcome = 'left'");
            var at = new Dictionary<int, int>();
            using var c = Cmd("SELECT step, COUNT(*) FROM tutorial_pilots WHERE outcome = 'left' AND step IS NOT NULL GROUP BY step");
            using var r = c.ExecuteReader();
            while (r.Read()) at[r.GetInt32(0)] = r.GetInt32(1);
            return (finished, left, at);
        }
    }
}
