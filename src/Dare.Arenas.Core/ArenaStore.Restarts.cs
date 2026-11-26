namespace Dare.Arenas.Core;

/// <summary>An owner's scheduled arena restart: when, how often it is announced (seconds, 0 = start and 10 s before), and their message.</summary>
public sealed record ScheduledRestart(string Arena, DateTimeOffset At, int EverySeconds, string? Message, string By);

// Scheduled restarts are kept so a server restart doesn't forget them (one per arena).
public sealed partial class ArenaStore
{
    public void SaveRestart(ScheduledRestart plan)
    {
        lock (_gate)
            Run("INSERT OR REPLACE INTO restart_plans(arena, at, every, message, by) VALUES($a,$at,$e,$m,$b)",
                ("$a", plan.Arena), ("$at", Fmt(plan.At)), ("$e", plan.EverySeconds),
                ("$m", (object?)plan.Message ?? DBNull.Value), ("$b", plan.By));
    }

    public void RemoveRestart(string arena)
    {
        lock (_gate) Run("DELETE FROM restart_plans WHERE arena = $a", ("$a", arena));
    }

    public IReadOnlyList<ScheduledRestart> Restarts()
    {
        var list = new List<ScheduledRestart>();
        lock (_gate)
        {
            using var c = Cmd("SELECT arena, at, every, message, by FROM restart_plans ORDER BY at");
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new(r.GetString(0), Parse(r.GetString(1)), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        }
        return list;
    }
}
