using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Restarting an owned arena from the web: which saved changes the running arena isn't using yet
// (map, graphics, settings that wait for a recycle), who is inside, an announcement, and an
// optional countdown with repeated announcements. Plans are kept in the store, so a server restart
// picks them up again (one that came due while the server was down is dropped: the arena just
// started fresh anyway). All of this runs on the mainloop.
public sealed partial class ArenaOwnership
{
    internal const int MaxAnnouncementLength = 200;
    private static readonly TimeSpan MaxRestartLead = TimeSpan.FromDays(7);
    private const int MinAnnounceEverySeconds = 30;

    private sealed class RestartPlan
    {
        public required string Arena;
        public required DateTimeOffset At;
        public required int EverySeconds; // 0: only at the start and 10 seconds before
        public required string? Message;
        public required string By;
        public DateTimeOffset NextAnnounce;
        public bool FinalSent;
    }

    // arena base name -> what changed since the running arena started
    private readonly Dictionary<string, List<string>> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RestartPlan> _restartPlans = new(StringComparer.OrdinalIgnoreCase);

    private void StartRestarts(IComponentBroker broker)
    {
        ArenaActionCallback.Register(broker, Callback_ArenaActionRestarts);
        var now = DateTimeOffset.UtcNow;
        foreach (var p in Store.Restarts())
        {
            if (p.At <= now) { Store.RemoveRestart(p.Arena); continue; }
            _restartPlans[p.Arena] = new RestartPlan
            {
                Arena = p.Arena, At = p.At, EverySeconds = p.EverySeconds, Message = p.Message, By = p.By,
                NextAnnounce = p.EverySeconds > 0 ? now.AddSeconds(p.EverySeconds) : DateTimeOffset.MaxValue,
            };
        }
        _mainloopTimer.SetTimer(TickRestarts, 1000, 1000, null);
    }

    private void StopRestarts(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(TickRestarts, null);
        ArenaActionCallback.Unregister(broker, Callback_ArenaActionRestarts);
        _restartPlans.Clear();
        _pendingChanges.Clear();
    }

    // A recycle (or the arena closing) means the next start reads everything fresh.
    private void Callback_ArenaActionRestarts(Arena arena, ArenaAction action)
    {
        if (action == ArenaAction.Destroy) _pendingChanges.Remove(arena.BaseName);
    }

    /// <summary>Notes a saved change the running arena won't see until it restarts. No-op when it isn't running.</summary>
    internal void MarkPendingRestart(string arenaName, string what)
    {
        if (RunningArenas(arenaName).Count == 0) return;
        if (!_pendingChanges.TryGetValue(arenaName, out var list)) _pendingChanges[arenaName] = list = new();
        if (!list.Contains(what, StringComparer.OrdinalIgnoreCase)) list.Add(what);
    }

    private List<Arena> RunningArenas(string name)
    {
        var running = new List<Arena>();
        _arenaManager.Lock();
        try
        {
            foreach (var a in _arenaManager.Arenas)
                if (a.Status == ArenaState.Running && string.Equals(a.BaseName, name, StringComparison.OrdinalIgnoreCase))
                    running.Add(a);
        }
        finally
        {
            _arenaManager.Unlock();
        }
        return running;
    }

    /// <summary>Pilots (not bots) in the arena right now, by name.</summary>
    private List<string> PilotsIn(string name)
    {
        var names = new List<string>();
        var bots = Store.BotNames();
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
                if (p.Arena is not null && string.Equals(p.Arena.BaseName, name, StringComparison.OrdinalIgnoreCase)
                    && p.Name is { } n && !bots.Contains(n))
                    names.Add(n);
        }
        finally
        {
            _playerData.Unlock();
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>The restart part of the owner page: running, who else is in (not <paramref name="viewer"/>), pending changes, a scheduled restart.</summary>
    internal object RestartData(string arena, string viewer)
    {
        var running = RunningArenas(arena).Count > 0;
        if (!running) _pendingChanges.Remove(arena);
        var pilots = PilotsIn(arena).Where(n => !string.Equals(n, viewer, StringComparison.OrdinalIgnoreCase)).ToList();
        _restartPlans.TryGetValue(arena, out var plan);
        return new
        {
            running,
            online = pilots.Count,
            onlineNames = pilots.Take(12),
            pending = _pendingChanges.TryGetValue(arena, out var list) ? list.ToArray() : Array.Empty<string>(),
            plan = plan is null ? null : new { at = plan.At, every = plan.EverySeconds, message = plan.Message, by = plan.By },
        };
    }

    /// <summary>
    /// Owner only. Restarts now (after an optional announcement), or schedules it for
    /// <paramref name="at"/> with announcements every <paramref name="everySeconds"/> (0: just at the
    /// start and 10 seconds before). A new plan replaces the old one.
    /// </summary>
    internal Result WebRestart(string player, string arena, string? message, DateTimeOffset? at, int everySeconds)
    {
        if (!HasRole(arena, player, ownerOnly: true))
            return new Result(false, "Only the arena's owner can restart it.");
        message = string.IsNullOrWhiteSpace(message) ? null : message.Trim().ReplaceLineEndings(" ");
        if (message is { Length: > MaxAnnouncementLength })
            return new Result(false, $"Announcements are at most {MaxAnnouncementLength} characters.");

        var now = DateTimeOffset.UtcNow;
        if (at is null || at.Value <= now.AddSeconds(2))
        {
            ForgetRestart(arena);
            if (message is not null) Announce(arena, $"{player}: {message}");
            return RestartNow(player, arena);
        }

        if (at.Value - now > MaxRestartLead)
            return new Result(false, "Pick a time within the next 7 days.");
        if (everySeconds != 0 && everySeconds < MinAnnounceEverySeconds)
            return new Result(false, $"Announce at most every {MinAnnounceEverySeconds} seconds.");

        var plan = new RestartPlan { Arena = arena, At = at.Value, EverySeconds = everySeconds, Message = message, By = player };
        _restartPlans[arena] = plan;
        Store.SaveRestart(new ScheduledRestart(arena, plan.At, everySeconds, message, player));
        AnnouncePlan(plan, now);
        plan.NextAnnounce = everySeconds > 0 ? now.AddSeconds(everySeconds) : DateTimeOffset.MaxValue;
        Log(LogLevel.Info, $"(web) {player} scheduled a restart of [{arena}] for {plan.At:u} (announce every {everySeconds}s).");
        return new Result(true, $"Restart scheduled for {plan.At:yyyy-MM-dd HH:mm} UTC ({RestartTimes.Until(plan.At - now)} from now).");
    }

    internal Result WebCancelRestart(string player, string arena)
    {
        if (!HasRole(arena, player, ownerOnly: true))
            return new Result(false, "Only the arena's owner can cancel its restart.");
        if (!ForgetRestart(arena))
            return new Result(true, "No restart was scheduled.");
        Announce(arena, "The scheduled arena restart is cancelled.");
        Log(LogLevel.Info, $"(web) {player} cancelled the scheduled restart of [{arena}].");
        return new Result(true, "Scheduled restart cancelled.");
    }

    private Result RestartNow(string player, string arena)
    {
        var (recycled, notRecycled) = RecycleRunning(arena);
        Log(LogLevel.Info, $"{player} restarted [{arena}]: {recycled.Count} recycled, {notRecycled.Count} could not be.");
        if (notRecycled.Count > 0)
            return new Result(false, $"Could not restart {string.Join(", ", notRecycled)} right now; type ?recyclearena there.");
        _pendingChanges.Remove(arena);
        return new Result(true, recycled.Count > 0
            ? "Arena restarted: players inside are back in with your changes."
            : "Nobody is in the arena; your changes load when someone enters.");
    }

    private bool TickRestarts()
    {
        if (_restartPlans.Count == 0) return true;
        var now = DateTimeOffset.UtcNow;
        foreach (var plan in _restartPlans.Values.ToList())
        {
            if (now >= plan.At)
            {
                ForgetRestart(plan.Arena);
                Announce(plan.Arena, "Restarting the arena now.");
                var result = RestartNow(plan.By + " (scheduled)", plan.Arena);
                if (!result.Ok) Log(LogLevel.Warn, $"Scheduled restart of [{plan.Arena}]: {result.Message}");
                continue;
            }
            var left = plan.At - now;
            if (!plan.FinalSent && left <= TimeSpan.FromSeconds(10))
            {
                plan.FinalSent = true;
                AnnouncePlan(plan, now);
            }
            else if (now >= plan.NextAnnounce && left > TimeSpan.FromSeconds(12))
            {
                AnnouncePlan(plan, now);
                while (plan.NextAnnounce <= now) plan.NextAnnounce = plan.NextAnnounce.AddSeconds(plan.EverySeconds);
            }
        }
        return true;
    }

    private bool ForgetRestart(string arena)
    {
        Store.RemoveRestart(arena);
        return _restartPlans.Remove(arena);
    }

    private void AnnouncePlan(RestartPlan plan, DateTimeOffset now) =>
        Announce(plan.Arena, $"Arena restarts in {RestartTimes.Until(plan.At - now)}" + (plan.Message is null ? "." : $": {plan.Message}"));

    private void Announce(string arena, string text)
    {
        foreach (var a in RunningArenas(arena))
            _chat.SendArenaMessage(a, ChatSound.Beep1, text);
    }
}
