namespace Dare.Arenas.Core;

/// <summary>
/// When the tutorial screen is sent again after the player entered the arena: a client drops LVZ toggles for objects it
/// hasn't finished downloading, so the (idempotent) toggles are repeated a few seconds in.
/// </summary>
public static class TutorialScreenResend
{
    public static readonly IReadOnlyList<TimeSpan> Delays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    /// <summary>True when the next of the timed re-sends (<paramref name="done"/> of them made so far) is due.</summary>
    public static bool Due(TimeSpan sinceEntry, int done) => done >= 0 && done < Delays.Count && sinceEntry >= Delays[done];
}
