namespace Dare.Arenas.Core;

public enum BotSeat { Out, Spectating, In }

public sealed record BotPlacement(string Label, BotSeat Seat, int Team);

/// <summary>
/// Decides, for one arena, which deployed bots should be in the game, which make room, and on which team,
/// from each team's match size and the real players flying on it. Pure; <see cref="BotSeats"/> paces the result.
/// </summary>
public static class BotDirector
{
    public static IReadOnlyList<BotPlacement> Plan(IReadOnlyList<ArenaBot> bots,
        IReadOnlyDictionary<int, int> matchSizes, IReadOnlyDictionary<int, int> playersFlying)
    {
        var result = new Dictionary<string, BotPlacement>(StringComparer.Ordinal);
        var deployed = bots.Where(b => b.Deployed).ToList();

        // Most wanted first: priority high to low, then label.
        static int Rank(ArenaBot a, ArenaBot b) =>
            a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : string.CompareOrdinal(a.Label, b.Label);

        var spares = new List<ArenaBot>();
        var short_ = new List<(int Team, int Missing)>();
        foreach (var group in deployed.GroupBy(b => b.Team).OrderBy(g => g.Key))
        {
            var team = group.Key;
            var ordered = group.ToList();
            ordered.Sort(Rank);
            if (!matchSizes.TryGetValue(team, out int size))
            {
                foreach (var b in ordered) result[b.Label] = new(b.Label, BotSeat.In, team);
                continue;
            }
            int places = Math.Max(0, size - playersFlying.GetValueOrDefault(team));
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i < places) result[ordered[i].Label] = new(ordered[i].Label, BotSeat.In, team);
                else spares.Add(ordered[i]);
            }
        }
        // A sized team with fewer bots than places is short (including teams with no bots at all).
        foreach (var (team, size) in matchSizes.OrderBy(p => p.Key))
        {
            int places = Math.Max(0, size - playersFlying.GetValueOrDefault(team));
            int have = deployed.Count(b => b.Team == team && result.TryGetValue(b.Label, out var p) && p.Seat == BotSeat.In);
            if (places > have) short_.Add((team, places - have));
        }

        // Spare bots, lowest priority first, fill the short teams.
        spares.Sort((a, b) => Rank(b, a));
        foreach (var (team, missing) in short_)
        {
            for (int n = 0; n < missing && spares.Count > 0; n++)
            {
                var spare = spares[0];
                spares.RemoveAt(0);
                result[spare.Label] = new(spare.Label, BotSeat.In, team);
            }
        }
        foreach (var spare in spares) result[spare.Label] = new(spare.Label, BotSeat.Out, spare.Team);

        return deployed.Select(b => result[b.Label]).ToList();
    }
}

/// <summary>
/// Turns <see cref="BotDirector.Plan"/> targets into paced changes: a bot leaving spectates now and goes 20–60 s later,
/// one coming (back) waits 3–15 s, a team change spectates on the new team then comes in, and a bot changes seat or
/// team at most once per 15 s.
/// </summary>
public sealed class BotSeats(Func<DateTimeOffset> clock, Random random)
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);

    private sealed class State
    {
        public BotSeat Seat;
        public int Team;
        public DateTimeOffset? LastChange;
        public (BotSeat Seat, int Team, DateTimeOffset Due)? Pending;
    }

    private readonly Dictionary<string, State> _bots = new(StringComparer.Ordinal);

    private TimeSpan Seconds(int min, int max) => TimeSpan.FromSeconds(random.Next(min, max + 1));

    public IReadOnlyList<BotPlacement> Step(IReadOnlyList<BotPlacement> target)
    {
        var now = clock();
        var seen = target.Select(t => t.Label).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _bots.Keys.Where(k => !seen.Contains(k)).ToList()) _bots.Remove(gone);

        var output = new List<BotPlacement>(target.Count);
        foreach (var t in target)
        {
            if (!_bots.TryGetValue(t.Label, out var st))
            {
                // First sight: a bot that should be in arrives from outside, after its wait.
                st = _bots[t.Label] = new State { Seat = t.Seat == BotSeat.Spectating ? BotSeat.Spectating : BotSeat.Out, Team = t.Team };
            }
            Advance(st, t, now);
            output.Add(new(t.Label, st.Seat, st.Team));
        }
        return output;
    }

    private void Advance(State st, BotPlacement t, DateTimeOffset now)
    {
        bool canChange = st.LastChange is null || now - st.LastChange.Value >= MinGap;
        bool changed = false;

        void Change(BotSeat seat, int team)
        {
            st.Seat = seat;
            st.Team = team;
            st.LastChange = now;
            st.Pending = null;
            changed = true;
        }

        if (st.Seat == t.Seat && st.Team == t.Team) { st.Pending = null; return; }

        if (t.Seat == BotSeat.In)
        {
            if (st.Seat == BotSeat.In)
            {
                // on the wrong team: step out of play onto the new team, then come in
                if (!canChange) return;
                Change(BotSeat.Spectating, t.Team);
                st.Pending = (BotSeat.In, t.Team, now + Seconds(3, 15));
                return;
            }
            if (st.Pending is not { Seat: BotSeat.In } p || p.Team != t.Team)
                st.Pending = (BotSeat.In, t.Team, now + Seconds(3, 15));
        }
        else
        {
            if (st.Seat == BotSeat.In)
            {
                if (!canChange) return;
                Change(BotSeat.Spectating, st.Team);
                if (t.Seat == BotSeat.Out) st.Pending = (BotSeat.Out, st.Team, now + Seconds(20, 60));
                return;
            }
            if (t.Seat == BotSeat.Out && st.Seat == BotSeat.Spectating && st.Pending is not { Seat: BotSeat.Out })
                st.Pending = (BotSeat.Out, st.Team, now + Seconds(20, 60));
            if (t.Seat == BotSeat.Out && st.Seat == BotSeat.Out) { st.Pending = null; return; }
        }

        if (!changed && canChange && st.Pending is { } due && now >= due.Due)
            Change(due.Seat, due.Team);
    }
}
