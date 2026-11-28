using System.Collections.Concurrent;
using System.Reflection;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;

namespace Dare.Arenas;

/// <summary>The names the tutorial's bots fly under; <see cref="FoundationAuth"/> lets them in on a bot pass.</summary>
internal static class TutorialBots
{
    private static readonly ConcurrentDictionary<string, byte> Names = new(StringComparer.OrdinalIgnoreCase);

    internal static void Hold(IEnumerable<string> names)
    {
        foreach (var name in names) Names[name] = 0;
    }

    internal static void Forget(IEnumerable<string> names)
    {
        foreach (var name in names) Names.TryRemove(name, out _);
    }

    internal static bool Holds(string name) => Names.ContainsKey(name);

    internal static void Clear() => Names.Clear();
}

/// <summary>Guest sessions (marked by <see cref="FoundationAuth"/>) and whether the guest's own client says the tutorial was done.</summary>
internal static class TutorialGuests
{
    /// <summary>The guest password a Dare client sends when, on that machine, the player left or finished the tutorial.</summary>
    internal const string DonePassword = "guest:done";

    private static readonly ConcurrentDictionary<Player, bool> Sessions = new();

    internal static void Mark(Player player, bool saysDone) => Sessions[player] = saysDone;

    internal static void Forget(Player player) => Sessions.TryRemove(player, out _);

    internal static bool IsGuest(Player player, out bool saysDone) => Sessions.TryGetValue(player, out saysDone);
}

// The in-game tutorial (docs/superpowers/specs/2026-11-27-ingame-tutorial-design.md). A newcomer's first entry to a
// public arena sends them to their own #trainingN arena, where a TutorialRun (Dare.Arenas.Core) walks them through the
// script; this file turns server callbacks into the run's events and the run's effects into server calls. All the
// tutorial's state is touched on the mainloop only: callbacks from the network threads queue their work there.
public sealed partial class ArenaOwnership
{
    private const string TutorialSection = "Tutorial";
    private const string TutorialOwner = "tutorial";
    private static readonly TimeSpan HintClientWait = TimeSpan.FromSeconds(2);
    /// <summary>Time between two mentor lines: one LVZ line shows at a time, so a step's lines take turns.</summary>
    private static readonly TimeSpan LinePace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SkipPopupShown = TimeSpan.FromSeconds(10);

    /// <summary>One player's tutorial, from arriving in the training arena until it ends.</summary>
    private sealed class TutorialSession
    {
        public required Player Player;
        public required string Name;
        public required string Arena;
        public required TutorialRun Run;
        public required bool Lean;
        public required bool Replay;
        public required string? Origin;
        /// <summary>An account whose tutorial isn't done yet: its progress and outcome are recorded.</summary>
        public required bool Records;
        public required IReadOnlyList<CastLine> WholeCast;
        public required IReadOnlyList<string> BotNames;
        /// <summary>The whole cast as the current step wants it, in <see cref="BotNames"/> order.</summary>
        public required IReadOnlyList<CastLine> CastNow;
        public required DateTimeOffset StartBy;
        public bool Started;
        public bool Finishing;
        public bool EngineOff;
        public bool BeaconOn;
        public short? LineShown;
        public readonly Queue<Say> Lines = new();
        public DateTimeOffset NextLineAt;
        public DateTimeOffset? SkipPopupUntil;
        // last position values queued (the position callback's thread only)
        public int LastRotation = -1, LastTileX = -1, LastTileY = -1;
        /// <summary>The screen was sent again on the first position packet (the position callback's thread sets it).</summary>
        public bool ScreenResent;
        public readonly HashSet<HudPart> Revealed = new();
    }

    /// <summary>A player on the way to a training arena, and how the run there starts.</summary>
    private sealed record TutorialTrip(string Arena, int StartStep, string? Origin, bool Replay, bool Records);

    /// <summary>A player who left a training arena mid-run without ?leave: ?go elsewhere (left) or a disconnect (dropout).</summary>
    private sealed record TutorialExit(string Name, bool Records);

    private TutorialScript? _tutorialScript;
    private ILvzObjects? _lvz;
    private readonly ConcurrentDictionary<Player, TutorialSession> _tutorials = new();
    private readonly ConcurrentDictionary<Player, byte> _hintClients = new();
    private readonly Dictionary<Player, TutorialTrip> _tutorialTrips = new();
    private readonly Dictionary<Player, TutorialExit> _tutorialExits = new();
    private readonly Dictionary<Player, DateTimeOffset> _tutorialOffers = new();
    private readonly HashSet<Player> _enteredThisLogin = new();

    private void LoadTutorial(IComponentBroker broker)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tutorial.script.json")
                ?? throw new FormatException("tutorial.script.json is not embedded");
            using var reader = new StreamReader(stream);
            _tutorialScript = TutorialScript.Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Tutorial off: {ex.Message}");
            _tutorialScript = null;
            return;
        }
        _lvz = broker.GetInterface<ILvzObjects>();
        if (_lvz is null) Log(LogLevel.Warn, "Tutorial: the LvzObjects module is not loaded; covers and mentor images won't show.");

        PlayerPositionPacketCallback.Register(broker, Tutorial_Position);
        KillCallback.Register(broker, Tutorial_Kill);
        SafeZoneCallback.Register(broker, Tutorial_SafeZone);
        GreenCallback.Register(broker, Tutorial_Green);
        ChatMessageCallback.Register(broker, Tutorial_Chat);
        ShipFreqChangeCallback.Register(broker, Tutorial_ShipFreq);
        FlagGainCallback.Register(broker, Tutorial_FlagGain);
        BallGoalCallback.Register(broker, Tutorial_BallGoal);
        SpawnCallback.Register(broker, Tutorial_Spawn);
        _mainloopTimer.SetTimer(TickTutorial, 1000, 1000, null);
    }

    private void UnloadTutorial(IComponentBroker broker)
    {
        if (_tutorialScript is null) return;
        _mainloopTimer.ClearTimer(TickTutorial, null);
        PlayerPositionPacketCallback.Unregister(broker, Tutorial_Position);
        KillCallback.Unregister(broker, Tutorial_Kill);
        SafeZoneCallback.Unregister(broker, Tutorial_SafeZone);
        GreenCallback.Unregister(broker, Tutorial_Green);
        ChatMessageCallback.Unregister(broker, Tutorial_Chat);
        ShipFreqChangeCallback.Unregister(broker, Tutorial_ShipFreq);
        FlagGainCallback.Unregister(broker, Tutorial_FlagGain);
        BallGoalCallback.Unregister(broker, Tutorial_BallGoal);
        SpawnCallback.Unregister(broker, Tutorial_Spawn);
        if (_lvz is not null) broker.ReleaseInterface(ref _lvz);
        _tutorials.Clear();
        _hintClients.Clear();
        _tutorialTrips.Clear();
        _tutorialExits.Clear();
        _tutorialOffers.Clear();
        _enteredThisLogin.Clear();
        TutorialBots.Clear();
        _tutorialScript = null;
    }

    [ConfigHelp<bool>(TutorialSection, "Enabled", ConfigScope.Global, Default = true,
        Description = "Whether newcomers are taken into the in-game tutorial (and ?tutorial works).")]
    [ConfigHelp<int>(TutorialSection, "FullCasts", ConfigScope.Global, Default = 10, Min = 0, Max = 100,
        Description = "Tutorials running at once that get the full bot cast; each one beyond gets one bot that plays every part.")]
    [ConfigHelp<int>(TutorialSection, "MaxTutorials", ConfigScope.Global, Default = 30, Min = 0, Max = 200,
        Description = "Most tutorials running at once; beyond it a newcomer is only offered the tutorial.")]
    private (bool Enabled, int FullCasts, int MaxTutorials) TutorialSettings() => (
        _configManager.GetInt(_configManager.Global, TutorialSection, "Enabled", 1) != 0,
        Math.Clamp(_configManager.GetInt(_configManager.Global, TutorialSection, "FullCasts", 10), 0, 100),
        Math.Clamp(_configManager.GetInt(_configManager.Global, TutorialSection, "MaxTutorials", 30), 0, 200));

    private int TutorialsRunning => _tutorials.Count + _tutorialTrips.Count;

    // ---- arriving, leaving ------------------------------------------------------------

    /// <summary>Mainloop (player actions are fired from the core's player processing).</summary>
    private void TutorialPlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.EnterArena && arena is not null)
        {
            var first = _enteredThisLogin.Add(player);
            try
            {
                if (_tutorialScript is not null) TutorialEnter(player, arena, first);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Tutorial: entry handling failed for {player.Name}: {ex.Message}");
            }
            // every account's first game entry is noted, whatever the tutorial did (or whether it is on): after
            // Decide above, so a new account's missing row was seen first; never again a newcomer after this
            if (first && player.Name is { } name && TutorialKindOf(player, out _) == EntryKind.Account)
                _accounts?.NoteGameLogin(name);
            return;
        }
        if (action == PlayerAction.Disconnect) _enteredThisLogin.Remove(player);
        if (_tutorialScript is null) return;
        try
        {
            switch (action)
            {
                case PlayerAction.LeaveArena when arena is not null:
                    // the zone drops a run before moving its player on, so a run still here means ?go elsewhere or a
                    // disconnect; which one shows next (an arena entry, or the disconnect that follows at once)
                    if (_tutorials.TryGetValue(player, out var s) && string.Equals(s.Arena, arena.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        DropRun(s);
                        _tutorialExits[player] = new(s.Name, s.Records);
                        // a Dare client closes its mentor box (it shows no arena list: the player chose where to go)
                        if (_hintClients.ContainsKey(player)) _chat.SendMessage(player, TutorialHint.End(false));
                    }
                    break;
                case PlayerAction.Disconnect:
                    _hintClients.TryRemove(player, out _);
                    _tutorialOffers.Remove(player);
                    _tutorialTrips.Remove(player);
                    if (_tutorials.TryGetValue(player, out var run))
                    {
                        DropRun(run);
                        RecordDropout(new(run.Name, run.Records));
                    }
                    else if (_tutorialExits.Remove(player, out var exit))
                        RecordDropout(exit);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Tutorial: {action} handling failed for {player.Name}: {ex.Message}");
        }
    }

    private void TutorialEnter(Player player, Arena arena, bool first)
    {
        // left a training arena with ?go and arrived somewhere: that's leaving, not dropping out
        if (_tutorialExits.Remove(player, out var exit) && exit.Records)
            _accounts?.SetTutorialOutcome(exit.Name, "left");

        if (TutorialCast.IsTutorialArena(arena.Name))
        {
            if (_tutorialTrips.Remove(player, out var trip) && string.Equals(trip.Arena, arena.Name, StringComparison.OrdinalIgnoreCase))
                BeginTutorial(player, arena, trip);
            return;
        }
        _tutorialTrips.Remove(player); // sent to a training arena but ended up elsewhere

        var (enabled, _, max) = TutorialSettings();
        if (!enabled || player.Name is not { } name || !player.IsStandard) return;
        var kind = TutorialKindOf(player, out var guestSaysDone);
        if (kind is null) return;
        var record = kind == EntryKind.Account ? _accounts?.TutorialOf(name) : null;
        var (what, act) = TutorialEntry.Decide(kind.Value, record, guestSaysDone, arena.IsPublic, first,
            TutorialCast.HasCapacity(TutorialsRunning, max));

        switch (what)
        {
            case TutorialStart.Start:
            case TutorialStart.Resume:
                var step = act > 0 ? TutorialRun.FirstStepOfAct(_tutorialScript!, act) : 0;
                SendToTutorial(player, Math.Max(step, 0), arena.Name, replay: false, records: kind == EntryKind.Account);
                break;
            case TutorialStart.Offer:
                // a Dare client says it shows hints right after entering; give it a moment to say so
                _tutorialOffers[player] = DateTimeOffset.UtcNow + HintClientWait;
                break;
        }
    }

    /// <summary>What is entering, for <see cref="TutorialEntry.Decide"/>; null when it is none of them (not logged in through Foundation).</summary>
    private static EntryKind? TutorialKindOf(Player player, out bool guestSaysDone)
    {
        guestSaysDone = false;
        if (IsBot(player)) return EntryKind.Bot;
        if (player.Type == ClientType.Fake) return EntryKind.Fake;
        if (TutorialGuests.IsGuest(player, out guestSaysDone)) return EntryKind.Guest;
        return player.Flags.Authenticated ? EntryKind.Account : null;
    }

    private void SendToTutorial(Player player, int startStep, string? origin, bool replay, bool records)
    {
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _tutorials.Values) inUse.Add(s.Arena);
        foreach (var t in _tutorialTrips.Values) inUse.Add(t.Arena);
        _arenaManager.Lock();
        try
        {
            foreach (var a in _arenaManager.Arenas)
                if (TutorialCast.IsTutorialArena(a.Name)) inUse.Add(a.Name);
        }
        finally { _arenaManager.Unlock(); }

        var arena = TutorialCast.Allocate(inUse);
        _tutorialOffers.Remove(player);
        _tutorialTrips[player] = new(arena, startStep, origin, replay, records);
        Log(LogLevel.Info, $"Tutorial: {player.Name} to [{arena}] at step {startStep + 1}{(replay ? " (replay)" : "")}.");
        _arenaManager.SendToArena(player, arena, 0, 0);
    }

    private void BeginTutorial(Player player, Arena arena, TutorialTrip trip)
    {
        var script = _tutorialScript!;
        var now = DateTimeOffset.UtcNow;
        // covers first, before the ship spawns (all LVZ objects start hidden)
        Toggle(player, TutorialLvz.AllCovers, true);
        _game.SetShipAndFreq(player, ShipType.Warbird, 0);

        var (_, fullCasts, _) = TutorialSettings();
        var lean = TutorialCast.Lean(_tutorials.Values.Count(s => !s.Lean), fullCasts);
        var whole = TutorialCast.WholeCast(script.Steps.Select(s => lean ? s.LeanCast : s.Cast), lean);
        var names = TutorialCast.PickNames(whole.Count, Random.Shared.Next, TutorialNameTaken);
        if (names.Count < whole.Count)
        {
            Log(LogLevel.Warn, $"Tutorial: only {names.Count} free bot names for [{arena.Name}] ({whole.Count} wanted).");
            whole = whole.Take(names.Count).ToList();
        }
        TutorialBots.Hold(names);

        var session = new TutorialSession
        {
            Player = player,
            Name = player.Name!,
            Arena = arena.Name,
            Run = new TutorialRun(script, () => DateTimeOffset.UtcNow, lean, trip.StartStep),
            Lean = lean,
            Replay = trip.Replay,
            Origin = trip.Origin,
            Records = trip.Records,
            WholeCast = whole,
            BotNames = names,
            CastNow = TutorialCast.ForStep(whole, [], lean), // the whole cast comes in spectating
            StartBy = now + HintClientWait,
        };
        _tutorials[player] = session;
        if (_hintClients.ContainsKey(player)) StartRun(session);
    }

    private bool TutorialNameTaken(string name) =>
        _playerData.FindPlayer(name) is not null || TutorialBots.Holds(name) ||
        _accounts?.Find(name) is not null || _accounts?.BotNameTaken(name) == true;

    private void StartRun(TutorialSession s)
    {
        if (s.Started) return;
        s.Started = true;
        // a run started part-way (a resumed act, ?tutorial swarm) shows what the steps before it revealed
        var earlier = _tutorialScript!.Steps.Take(s.Run.StepIndex).SelectMany(st => st.Reveal).Distinct();
        Apply(s, [.. earlier.Select(p => new RevealPart(p)), .. s.Run.Start()]);
        if (!s.Run.Done) ShowSkipPopup(s); // the skip/leave popup shows at the start
        RecordProgress(s);
    }

    /// <summary>The run stops (its bots with it); the player is not moved.</summary>
    private void DropRun(TutorialSession s)
    {
        _tutorials.TryRemove(s.Player, out _);
        TutorialBots.Forget(s.BotNames);
    }

    /// <summary>Finished or ?leave: the screen is cleared, the outcome recorded, and the player sent on.</summary>
    private void EndTutorial(TutorialSession s, string outcome)
    {
        var player = s.Player;
        DropRun(s);
        if (player.Arena is { } here && string.Equals(here.Name, s.Arena, StringComparison.OrdinalIgnoreCase))
        {
            var off = new List<short>(TutorialLvz.AllCovers)
            {
                TutorialLvz.MentorBox, TutorialLvz.Portrait, TutorialLvz.PortraitTalking,
                TutorialLvz.SkipPopup, TutorialLvz.TryPopup, TutorialLvz.Beacon,
            };
            if (s.LineShown is { } line) off.Add(line);
            Toggle(player, off, false);
        }
        if (s.Records)
        {
            _accounts?.SetTutorialProgress(s.Name, s.Run.Done ? _tutorialScript!.Steps.Count : s.Run.StepIndex + 1, CurrentAct(s));
            _accounts?.SetTutorialOutcome(s.Name, outcome);
        }
        Log(LogLevel.Info, $"Tutorial: {s.Name} {outcome} at step {Math.Min(s.Run.StepIndex + 1, _tutorialScript!.Steps.Count)} in [{s.Arena}].");

        var (arena, nobodyOn) = TutorialDestination.Pick(ArenaPilots(player), s.Origin, s.Replay);
        // the Dare client closes the mentor and, with nobody on, opens the arena list; Continuum gets a line instead
        if (_hintClients.ContainsKey(player)) _chat.SendMessage(player, TutorialHint.End(nobodyOn));
        else if (nobodyOn) _chat.SendMessage(player, NobodyOnLine());
        _arenaManager.SendToArena(player, arena, 0, 0);
    }

    private int CurrentAct(TutorialSession s) =>
        s.Run.ActOf(Math.Min(s.Run.StepIndex, _tutorialScript!.Steps.Count - 1));

    private void RecordProgress(TutorialSession s)
    {
        if (s.Records && !s.Run.Done) _accounts?.SetTutorialProgress(s.Name, s.Run.StepIndex + 1, CurrentAct(s));
    }

    private void RecordDropout(TutorialExit exit)
    {
        if (!exit.Records || _accounts is null) return;
        if (_accounts.AddTutorialDropout(exit.Name) >= TutorialEntry.MaxDropouts)
            _accounts.SetTutorialOutcome(exit.Name, "left");
        Log(LogLevel.Info, $"Tutorial: {exit.Name} dropped out.");
    }

    /// <summary>Real pilots (no bots, no fake players) per arena a newcomer could be sent to, leaving out <paramref name="self"/>.</summary>
    private List<(string Arena, int RealPilots)> ArenaPilots(Player self)
    {
        var counts = new Dictionary<string, (string BaseName, int Count)>(StringComparer.OrdinalIgnoreCase);
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
            {
                if (p == self || p.Status != PlayerState.Playing || p.Arena is not { } a || IsBot(p) || p.Type == ClientType.Fake) continue;
                counts[a.Name] = (a.BaseName, counts.GetValueOrDefault(a.Name).Count + 1);
            }
        }
        finally { _playerData.Unlock(); }
        return counts.Where(kv => !DraftRules.IsDraft(kv.Value.BaseName) && !Store.IsPrivate(kv.Value.BaseName))
            .Select(kv => (kv.Key, kv.Value.Count)).ToList();
    }

    /// <summary>For Continuum, when nobody is on: where the bots fly, or else how to see the arenas.</summary>
    private string NobodyOnLine()
    {
        var botArenas = _hosts
            .Where(h => h.Value.Owner != TutorialOwner && h.Value.Shown.Any(p => p.Seat == BotSeat.In) &&
                !DraftRules.IsDraft(h.Key) && !h.Key.StartsWith('#') && !Store.IsPrivate(h.Key))
            .Select(h => h.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (botArenas.Count > 0)
            return $"Nobody else is flying right now. Bots fly in {string.Join(", ", botArenas)}: type ?go {botArenas[0]} to join them.";
        var pointer = _tutorialScript!.Prompts.FirstOrDefault(p => p.Id == "arena_pointer");
        return pointer is null ? "Type ?arena to see the arenas." : TutorialScript.ForContinuum(pointer.Text, _tutorialScript.ContinuumKeys);
    }

    // ---- the clock ------------------------------------------------------------------------

    private bool TickTutorial()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (player, due) in _tutorialOffers.ToArray())
                if (now >= due) SendOffer(player);

            foreach (var s in _tutorials.Values.ToArray())
            {
                if (!s.Started)
                {
                    if (now >= s.StartBy) StartRun(s);
                    continue;
                }
                if (s.Finishing)
                {
                    PumpLines(s, now);
                    if (s.Lines.Count == 0 && now >= s.NextLineAt) EndTutorial(s, "finished");
                    continue;
                }
                PumpLines(s, now);
                // no Tick while the mentor still has lines queued: a step that moves on by itself can't leave before its
                // lines have shown (its pause still counts from the step's start; checks the player meets still move on)
                if (s.Lines.Count == 0) Feed(s, new Tick());
                if (s.SkipPopupUntil is { } until && now >= until)
                {
                    s.SkipPopupUntil = null;
                    Toggle(s.Player, [TutorialLvz.SkipPopup], false);
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Tutorial tick: {ex.Message}");
        }
        return true;
    }

    private void SendOffer(Player player)
    {
        _tutorialOffers.Remove(player);
        if (player.Status != PlayerState.Playing || _tutorialScript is not { } script) return;
        // the try popup is an LVZ object of the training arena, not served here: Continuum gets the chat line
        if (script.Prompts.FirstOrDefault(p => p.Id == "try_tutorial") is { } line)
            _chat.SendMessage(player, TutorialScript.ForContinuum(line.Text, script.ContinuumKeys));
        if (_hintClients.ContainsKey(player)) _chat.SendMessage(player, TutorialHint.PromptTry());
    }

    // ---- events in, effects out -------------------------------------------------------------

    private void Feed(TutorialSession s, TutorialEvent e)
    {
        if (!s.Started || s.Finishing || s.Run.Done || !_tutorials.TryGetValue(s.Player, out var current) || current != s) return;
        var before = s.Run.StepIndex;
        Apply(s, s.Run.Handle(e));
        if (s.Run.StepIndex != before)
        {
            RecordProgress(s);
            if (s.SkipPopupUntil is not null)
            {
                s.SkipPopupUntil = null;
                Toggle(s.Player, [TutorialLvz.SkipPopup], false);
            }
        }
    }

    private void Apply(TutorialSession s, IReadOnlyList<TutorialEffect> effects)
    {
        var player = s.Player;
        var hints = _hintClients.ContainsKey(player);
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case Say say:
                    s.Lines.Enqueue(say);
                    break;
                case RevealPart reveal:
                    s.Revealed.Add(reveal.Part);
                    Toggle(player, TutorialLvz.CoversOf(reveal.Part), false);
                    if (hints) _chat.SendMessage(player, TutorialHint.Reveal(reveal.Part));
                    break;
                case Grant grant:
                    GiveGrant(player, grant.Prize);
                    break;
                case EngineOff:
                    if (!s.EngineOff) _game.GivePrize(player, Prize.Shutdown, 1);
                    s.EngineOff = true;
                    break;
                case EngineOn:
                    // a ship reset ends the shutdown (and the act's prizes with it); only when the engines were off
                    if (s.EngineOff) _game.ShipReset(player);
                    s.EngineOff = false;
                    break;
                case SetCast cast:
                    s.CastNow = TutorialCast.ForStep(s.WholeCast, cast.Cast, s.Lean);
                    break;
                case ShowSkipPrompt:
                    ShowSkipPopup(s);
                    break;
                case Finished:
                    s.Finishing = true;
                    break;
            }
        }

        var reach = !s.Run.Done && _tutorialScript!.Steps[s.Run.StepIndex].Check.Kind == CheckKind.Reach;
        if (reach != s.BeaconOn)
        {
            s.BeaconOn = reach;
            Toggle(player, [TutorialLvz.Beacon], reach);
        }
        PumpLines(s, DateTimeOffset.UtcNow);
    }

    private void GiveGrant(Player player, string name)
    {
        if (Enum.TryParse<Prize>(name, true, out var prize) && Enum.IsDefined(prize))
            _game.GivePrize(player, prize, 1);
        else
            Log(LogLevel.Warn, $"Tutorial: unknown prize '{name}' in the script.");
    }

    /// <summary>
    /// Sends the screen's state again: the covers (on, or off once revealed), the beacon and, for Continuum, the mentor
    /// box with its current line and the skip popup. Toggles made in the arena-entry callback can reach the client
    /// before it has the LVZ file; by its first position packet it is in the game. Toggles are idempotent.
    /// </summary>
    private void ResendScreen(TutorialSession s)
    {
        var revealed = s.Revealed.SelectMany(TutorialLvz.CoversOf).ToHashSet();
        var toggles = TutorialLvz.AllCovers.Select(id => new LvzObjectToggle(id, !revealed.Contains(id))).ToList();
        toggles.Add(new(TutorialLvz.Beacon, s.BeaconOn));
        if (!_hintClients.ContainsKey(s.Player))
        {
            if (s.LineShown is { } line)
            {
                toggles.Add(new(TutorialLvz.MentorBox, true));
                toggles.Add(new(TutorialLvz.Portrait, true));
                toggles.Add(new(line, true));
            }
            if (s.SkipPopupUntil is not null) toggles.Add(new(TutorialLvz.SkipPopup, true));
        }
        Toggle(s.Player, toggles);
    }

    /// <summary>Shows the next queued mentor line once the one before has had its time.</summary>
    private void PumpLines(TutorialSession s, DateTimeOffset now)
    {
        if (s.Lines.Count == 0 || now < s.NextLineAt) return;
        var script = _tutorialScript!;
        var say = s.Lines.Dequeue();
        var player = s.Player;
        s.NextLineAt = now + LinePace;
        // the chat copy, for everyone, so the line is in the log
        _chat.SendMessage(player, $"{script.MentorName}: {TutorialScript.ForContinuum(say.Line.Text, script.ContinuumKeys)}");
        if (_hintClients.ContainsKey(player))
        {
            _chat.SendMessage(player, TutorialHint.Say(say.Step, say.Of, say.Line.Text));
            return;
        }
        var line = TutorialLvz.Line(say.Line);
        var toggles = new List<LvzObjectToggle>
        {
            new(TutorialLvz.MentorBox, true), new(TutorialLvz.Portrait, true), new(line, true),
        };
        if (s.LineShown is { } previous && previous != line) toggles.Add(new(previous, false));
        s.LineShown = line;
        Toggle(player, toggles);
    }

    private void ShowSkipPopup(TutorialSession s)
    {
        var script = _tutorialScript!;
        if (script.Prompts.FirstOrDefault(p => p.Id == "skip_popup") is { } line)
            _chat.SendMessage(s.Player, TutorialScript.ForContinuum(line.Text, script.ContinuumKeys));
        if (_hintClients.ContainsKey(s.Player))
        {
            _chat.SendMessage(s.Player, TutorialHint.PromptSkip());
            return;
        }
        Toggle(s.Player, [TutorialLvz.SkipPopup], true);
        s.SkipPopupUntil = DateTimeOffset.UtcNow + SkipPopupShown;
    }

    private void Toggle(Player player, IEnumerable<short> ids, bool on) =>
        Toggle(player, ids.Select(id => new LvzObjectToggle(id, on)).ToList());

    private void Toggle(Player player, List<LvzObjectToggle> toggles)
    {
        if (_lvz is null || toggles.Count == 0) return;
        _lvz.Toggle(player, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(toggles));
    }

    /// <summary>Runs <paramref name="work"/> on the mainloop with the player's run, if they still have that run.</summary>
    private void OnRun(Player player, Action<TutorialSession> work)
    {
        if (!_tutorials.TryGetValue(player, out var s)) return;
        _mainloop.QueueMainWorkItem(state =>
        {
            if (!_tutorials.TryGetValue(state.Session.Player, out var current) || current != state.Session) return;
            try { state.Work(state.Session); }
            catch (Exception ex) { Log(LogLevel.Error, $"Tutorial: event for {state.Session.Name} failed: {ex.Message}"); }
        }, (Session: s, Work: work));
    }

    // ---- server callbacks (any thread) ------------------------------------------------------

    private void Tutorial_Position(Player player, ref readonly C2S_PositionPacket pos, ref readonly ExtraPositionData extra, bool hasExtra)
    {
        if (!_tutorials.TryGetValue(player, out var s)) return;
        if (!s.ScreenResent)
        {
            s.ScreenResent = true;
            OnRun(player, ResendScreen);
        }
        if (!s.Started || player.Ship == ShipType.Spec) return;
        int rotation = pos.Rotation, tileX = pos.X >> 4, tileY = pos.Y >> 4;
        var weapon = pos.Weapon;
        string? fired = weapon.Type switch
        {
            WeaponCodes.Bullet or WeaponCodes.BounceBullet => "bullet",
            WeaponCodes.Bomb or WeaponCodes.ProxBomb => weapon.Alternate ? "mine" : "bomb",
            WeaponCodes.Repel => "repel",
            _ => null,
        };
        var turned = rotation != s.LastRotation;
        var moved = tileX != s.LastTileX || tileY != s.LastTileY;
        if (!turned && !moved && fired is null) return;
        s.LastRotation = rotation;
        s.LastTileX = tileX;
        s.LastTileY = tileY;
        OnRun(player, run =>
        {
            if (turned) Feed(run, new Rotated(rotation));
            if (moved) Feed(run, new Moved(tileX, tileY));
            if (fired is not null) Feed(run, new Fired(fired));
        });
    }

    private void Tutorial_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        var name = killed.Name;
        if (name is not null && IsBot(killed))
            OnRun(killer, run =>
            {
                var i = run.BotNames.ToList().FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i < run.CastNow.Count) Feed(run, new Killed(run.CastNow[i].Role));
            });
        OnRun(killed, run => Feed(run, new Died()));
    }

    private void Tutorial_SafeZone(Player player, int x, int y, bool entering)
    {
        if (entering) OnRun(player, run => Feed(run, new EnteredSafe()));
    }

    private void Tutorial_Green(Player player, int x, int y, Prize prize) => OnRun(player, run => Feed(run, new PickedGreen()));

    private void Tutorial_Chat(Arena? arena, Player? player, ChatMessageType type, ChatSound sound, Player? toPlayer, short freq, ReadOnlySpan<char> message)
    {
        if (player is not null && type is ChatMessageType.Pub or ChatMessageType.PubMacro)
            OnRun(player, run => Feed(run, new SaidPublic()));
    }

    private void Tutorial_FlagGain(Arena arena, Player player, short flagId, FlagPickupReason reason) =>
        OnRun(player, run => Feed(run, new GotFlag()));

    private void Tutorial_BallGoal(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates) =>
        OnRun(player, run => Feed(run, new ScoredGoal()));

    private void Tutorial_ShipFreq(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq) =>
        OnRun(player, run =>
        {
            var shipStep = _tutorialScript!.Steps.ToList().FindIndex(st => st.Check.Kind == CheckKind.ShipChange);
            var shipStepReached = shipStep >= 0 && run.Started && run.Run.StepIndex >= shipStep;
            // the tutorial flies a Warbird on freq 0 (the bots' teams assume it); spectating is put back too
            if (newShip == ShipType.Spec || (!shipStepReached && newShip != ShipType.Warbird))
            {
                _game.SetShipAndFreq(player, ShipType.Warbird, 0);
                return;
            }
            if (newFreq != 0) _game.SetFreq(player, 0);
            if (newShip != oldShip && shipStepReached) Feed(run, new ChangedShip((int)newShip));
        });

    private void Tutorial_Spawn(Player player, SpawnCallback.SpawnReason reason)
    {
        // a respawn brings the engines back and loses the act's prizes: while the act has the engines off (the swarm),
        // give back the shutdown and what the act has given so far, or a player killed at "repel" has no Repel
        if ((reason & (SpawnCallback.SpawnReason.AfterDeath | SpawnCallback.SpawnReason.ShipChange | SpawnCallback.SpawnReason.Initial)) == 0) return;
        OnRun(player, run =>
        {
            if (!run.EngineOff || run.Run.Done) return;
            _game.GivePrize(player, Prize.Shutdown, 1);
            foreach (var grant in TutorialRun.ActGrants(_tutorialScript!, run.Run.StepIndex)) GiveGrant(player, grant);
        });
    }

    // ---- ReconcileBots ----------------------------------------------------------------------

    /// <summary>Each running tutorial's bots and where its current step wants them (mainloop).</summary>
    private IEnumerable<(string Arena, IReadOnlyList<ArenaBot> Bots, IReadOnlyList<BotPlacement> Placements)> TutorialLineups()
    {
        foreach (var s in _tutorials.Values)
        {
            if (s.BotNames.Count == 0) continue;
            var lineup = TutorialCast.Lineup(s.Arena, s.CastNow, s.BotNames);
            yield return (s.Arena, lineup.Select(l => l.Bot).ToList(), lineup.Select(l => l.Placement).ToList());
        }
    }

    // ---- commands -----------------------------------------------------------------------------

    [CommandHelp(
        Targets = CommandTarget.None,
        Args = "[controls|swarm|objectives]",
        Description = """
            Plays the flight tutorial in a training arena of your own, then brings you back here.
            Give a part to start there: controls (flying and shooting), swarm (radar, items, a fight),
            objectives (safe zone, greens, chat, ships, flag and ball).
            """)]
    private void Command_tutorial(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var part = parameters.Trim().ToString();
        _mainloop.QueueMainWorkItem(StartTutorialCommand, (player, part));
    }

    private void StartTutorialCommand((Player Player, string Part) args)
    {
        var (player, part) = args;
        if (_tutorialScript is not { } script || !TutorialSettings().Enabled)
        {
            Reply(player, "The tutorial is off right now.");
            return;
        }
        if (player.Name is not { } name || !player.IsStandard || TutorialKindOf(player, out _) is not (EntryKind.Account or EntryKind.Guest)) return;
        if (_tutorials.ContainsKey(player) || _tutorialTrips.ContainsKey(player))
        {
            Reply(player, "You're in the tutorial already. ?skip skips a step, ?leave ends it.");
            return;
        }
        var step = 0;
        if (part.Length > 0)
        {
            step = TutorialEntry.ActNamed(part) is { } act ? TutorialRun.FirstStepOfAct(script, act) : -1;
            if (step < 0)
            {
                Reply(player, "Type ?tutorial for the whole tutorial, or ?tutorial controls, ?tutorial swarm or ?tutorial objectives for one part.");
                return;
            }
        }
        if (!TutorialCast.HasCapacity(TutorialsRunning, TutorialSettings().MaxTutorials))
        {
            Reply(player, "The training arenas are full right now. Try again in a few minutes.");
            return;
        }
        var records = TutorialKindOf(player, out _) == EntryKind.Account && _accounts?.TutorialOf(name) is { Outcome: null };
        SendToTutorial(player, step, player.Arena?.Name, replay: true, records);
    }

    [CommandHelp(Targets = CommandTarget.None, Description = "In the tutorial: skips the current step.")]
    private void Command_skip(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target) =>
        _mainloop.QueueMainWorkItem(p =>
        {
            if (!_tutorials.TryGetValue(p, out var s))
            {
                Reply(p, "You're not in the tutorial. Type ?tutorial to play it.");
                return;
            }
            if (!s.Started) StartRun(s);
            else Feed(s, new Skip());
        }, player);

    [CommandHelp(Targets = CommandTarget.None, Description = "In the tutorial: ends it and takes you on to the game.")]
    private void Command_leave(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target) =>
        _mainloop.QueueMainWorkItem(p =>
        {
            if (!_tutorials.TryGetValue(p, out var s))
            {
                Reply(p, "You're not in the tutorial. Type ?tutorial to play it.");
                return;
            }
            Reply(p, "You left the tutorial. Type ?tutorial any time to see it again.");
            EndTutorial(s, s.Run.Done || s.Finishing ? "finished" : "left");
        }, player);

    [CommandHelp(Targets = CommandTarget.None, Description = "Sent by the Dare client on entering an arena: it shows the tutorial's mentor itself.")]
    private void Command_tutorialclient(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target) =>
        _mainloop.QueueMainWorkItem(p =>
        {
            if (p.Status is not (PlayerState.Playing or PlayerState.LoggedIn)) return;
            _hintClients[p] = 0;
            if (_tutorials.TryGetValue(p, out var s) && !s.Started) StartRun(s);
            if (_tutorialOffers.ContainsKey(p)) SendOffer(p);
        }, player);

    [CommandHelp(Targets = CommandTarget.None, Description = "Staff: how many finished or left the tutorial, and where those who left stopped.")]
    private void Command_tutorialstats(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!_capabilityManager.HasCapability(player, ApproveCapability))
        {
            Reply(player, "Tutorial stats are for zone staff.");
            return;
        }
        if (_accounts is null) return;
        var (finished, left, at) = _accounts.TutorialCounts();
        Reply(player, $"Tutorial: {finished} finished, {left} left, {_tutorials.Count} running now.");
        if (at.Count > 0)
            Reply(player, "Tutorial: left at step " + string.Join(", ", at.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}")) + ".");
    }
}
