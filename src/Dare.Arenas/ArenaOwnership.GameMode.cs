using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// GameMode:Attach: an owner-editable list of allow-listed scoring/rule modules (GameModeAttach) that
// this plugin attaches to the arena when it's created and whenever its settings change, and detaches
// when removed. Modules:AttachModules stays staff-only; modules it attaches are never detached here.
// Spec: docs/superpowers/specs/2026-11-28-game-mode-phase2-wizard-design.md. Mainloop only.
public sealed partial class ArenaOwnership
{
    private IModuleManager? _moduleManager;
    private readonly Dictionary<Arena, HashSet<string>> _gameModeAttached = new();
    private readonly Dictionary<Arena, string> _gameModeWarned = new();

    private void StartGameMode(IComponentBroker broker)
    {
        _moduleManager = broker.GetInterface<IModuleManager>();
        if (_moduleManager is null) { Log(LogLevel.Error, "GameMode:Attach off: no module manager."); return; }
        ArenaActionCallback.Register(broker, Callback_ArenaActionGameMode);
        StartFlagHold(broker);
        StartKingReward(broker);
    }

    private void StopGameMode(IComponentBroker broker)
    {
        if (_moduleManager is null) return;
        ArenaActionCallback.Unregister(broker, Callback_ArenaActionGameMode);
        StopFlagHold(broker);
        StopKingReward(broker);
        broker.ReleaseInterface(ref _moduleManager);
        _gameModeAttached.Clear();
        _gameModeWarned.Clear();
    }

    private void Callback_ArenaActionGameMode(Arena arena, ArenaAction action)
    {
        if (action == ArenaAction.Destroy)
        {
            _gameModeAttached.Remove(arena);
            _gameModeWarned.Remove(arena);
            StopFlagRule(arena);
            _rounds.Remove(arena);
            _kingRewards.Remove(arena);
            return;
        }
        if (action is not (ArenaAction.Create or ArenaAction.ConfChanged) || arena.Cfg is not { } cfg) return;
        if (_flagRules.TryGetValue(arena, out var rule)) ReadFlagRule(arena, rule);
        ApplyGameModeAttach(arena);
        ApplyRound(arena, cfg, created: action == ArenaAction.Create);
        ReadKingReward(arena, cfg);
    }

    // The server reads a spawn point per ball (Balls.cs: SpawnX, SpawnX1..SpawnX7) but documents only the first,
    // so owners couldn't place balls 2-8: documenting them here puts them on the owner allow-list (Phase 3 labeller).
    [ConfigHelp<int>("Soccer", "SpawnX1", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 2 spawns: x in tiles (balls spawn in order; ball 2 needs ball 1's).")]
    [ConfigHelp<int>("Soccer", "SpawnY1", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 2 spawns: y in tiles.")]
    [ConfigHelp<int>("Soccer", "SpawnRadius1", ConfigScope.Arena, Min = 0, Max = 1024, Description = "How far from its spawn point ball 2 may appear (tiles).")]
    [ConfigHelp<int>("Soccer", "SpawnX2", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 3 spawns: x in tiles (balls spawn in order; ball 3 needs ball 2's).")]
    [ConfigHelp<int>("Soccer", "SpawnY2", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 3 spawns: y in tiles.")]
    [ConfigHelp<int>("Soccer", "SpawnRadius2", ConfigScope.Arena, Min = 0, Max = 1024, Description = "How far from its spawn point ball 3 may appear (tiles).")]
    [ConfigHelp<int>("Soccer", "SpawnX3", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 4 spawns: x in tiles (balls spawn in order; ball 4 needs ball 3's).")]
    [ConfigHelp<int>("Soccer", "SpawnY3", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 4 spawns: y in tiles.")]
    [ConfigHelp<int>("Soccer", "SpawnRadius3", ConfigScope.Arena, Min = 0, Max = 1024, Description = "How far from its spawn point ball 4 may appear (tiles).")]
    [ConfigHelp<int>("Soccer", "SpawnX4", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 5 spawns: x in tiles (balls spawn in order; ball 5 needs ball 4's).")]
    [ConfigHelp<int>("Soccer", "SpawnY4", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 5 spawns: y in tiles.")]
    [ConfigHelp<int>("Soccer", "SpawnRadius4", ConfigScope.Arena, Min = 0, Max = 1024, Description = "How far from its spawn point ball 5 may appear (tiles).")]
    [ConfigHelp<int>("Soccer", "SpawnX5", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 6 spawns: x in tiles (balls spawn in order; ball 6 needs ball 5's).")]
    [ConfigHelp<int>("Soccer", "SpawnY5", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 6 spawns: y in tiles.")]
    [ConfigHelp<int>("Soccer", "SpawnRadius5", ConfigScope.Arena, Min = 0, Max = 1024, Description = "How far from its spawn point ball 6 may appear (tiles).")]
    [ConfigHelp<int>("Soccer", "SpawnX6", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 7 spawns: x in tiles (balls spawn in order; ball 7 needs ball 6's).")]
    [ConfigHelp<int>("Soccer", "SpawnY6", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 7 spawns: y in tiles.")]
    [ConfigHelp<int>("Soccer", "SpawnRadius6", ConfigScope.Arena, Min = 0, Max = 1024, Description = "How far from its spawn point ball 7 may appear (tiles).")]
    [ConfigHelp<int>("Soccer", "SpawnX7", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 8 spawns: x in tiles (balls spawn in order; ball 8 needs ball 7's).")]
    [ConfigHelp<int>("Soccer", "SpawnY7", ConfigScope.Arena, Min = 0, Max = 1023, Description = "Where ball 8 spawns: y in tiles.")]
    [ConfigHelp<int>("Soccer", "SpawnRadius7", ConfigScope.Arena, Min = 0, Max = 1024, Description = "How far from its spawn point ball 8 may appear (tiles).")]
    [ConfigHelp<GameModeModules>("GameMode", "Attach", ConfigScope.Arena, Default = GameModeModules.None,
        Description = "Scoring and rules this arena runs: Kills, Flags, Balls, King, Ships, ShipChange (names separated by commas).")]
    private void ApplyGameModeAttach(Arena arena)
    {
        if (_moduleManager is not { } modules || arena.Cfg is not { } cfg) return;
        var wanted = GameModeAttach.Parse(_configManager.GetStr(cfg, "GameMode", "Attach"), out var unknown);
        if (unknown.Count > 0)
        {
            var text = string.Join(", ", unknown);
            if (!_gameModeWarned.TryGetValue(arena, out var last) || last != text)
            {
                _gameModeWarned[arena] = text;
                Log(LogLevel.Warn, $"{{{arena.Name}}} GameMode:Attach: ignored unknown {text}.");
            }
        }
        var staff = GameModeAttach.StaffAttached(_configManager.GetStr(cfg, "Modules", "AttachModules"));
        if (!_gameModeAttached.TryGetValue(arena, out var ours)) _gameModeAttached[arena] = ours = new(StringComparer.OrdinalIgnoreCase);
        var (attach, detach) = GameModeAttach.Diff(ours, wanted, staff);
        if (attach.Count == 0 && detach.Count == 0) return;
        foreach (var t in attach) ours.Add(t);
        foreach (var t in detach) ours.Remove(t);
        // The flag rule is the plugin's own (ArenaOwnership.FlagHold.cs), not a server module.
        var cmp = StringComparer.OrdinalIgnoreCase;
        if (detach.Contains(FlagHold.TypeName, cmp)) StopFlagRule(arena);
        if (attach.Contains(FlagHold.TypeName, cmp))
        {
            if (staff.Contains("SS.Core.Modules.Scoring.FlagGamePoints"))
            {
                ours.Remove(FlagHold.TypeName);
                Log(LogLevel.Warn, $"{{{arena.Name}}} GameMode: flag rule not started: staff attached FlagGamePoints.");
            }
            else StartFlagRule(arena);
        }
        attach = attach.Where(t => !cmp.Equals(t, FlagHold.TypeName)).ToList();
        detach = detach.Where(t => !cmp.Equals(t, FlagHold.TypeName)).ToList();
        if (attach.Count > 0 || detach.Count > 0) _ = ChangeModulesAsync(modules, arena, attach, detach);
    }

    private async Task ChangeModulesAsync(IModuleManager modules, Arena arena, IReadOnlyList<string> attach, IReadOnlyList<string> detach)
    {
        foreach (var t in detach)
        {
            try
            {
                var ok = await modules.DetachModuleAsync(t, arena).ConfigureAwait(false);
                Log(ok ? LogLevel.Info : LogLevel.Warn, $"{{{arena.Name}}} GameMode: {(ok ? "detached" : "could not detach")} {t}.");
                if (!ok) Settle(arena, t, attaching: false);
            }
            catch (Exception ex) { Log(LogLevel.Error, $"{{{arena.Name}}} GameMode: detaching {t} failed: {ex.Message}"); Settle(arena, t, attaching: false); }
        }
        foreach (var t in attach)
        {
            try
            {
                var ok = await modules.AttachModuleAsync(t, arena).ConfigureAwait(false);
                Log(ok ? LogLevel.Info : LogLevel.Warn, $"{{{arena.Name}}} GameMode: {(ok ? "attached" : "could not attach")} {t}.");
                if (!ok) Settle(arena, t, attaching: true);
                else if (GameModeAttach.NeedsCreate(t)) ReplayCreate(arena, t);
            }
            catch (Exception ex) { Log(LogLevel.Error, $"{{{arena.Name}}} GameMode: attaching {t} failed: {ex.Message}"); Settle(arena, t, attaching: true); }
        }
    }

    /// <summary>
    /// Koth and BallGamePoints set up on the arena's Create, which this arena already had (GameModeAttach.NeedsCreate):
    /// give it to the arena's own handlers only (the attached modules), on the mainloop, so the zone's other modules
    /// don't see a second Create. The server's Modules:AttachModules attaches before Create, so it never needs this.
    /// </summary>
    private void ReplayCreate(Arena arena, string type)
    {
        // The attach can finish while the arena is still starting up (its Create has fired, it isn't Running yet):
        // check every 100 ms, up to 30 s, and give up if the arena closes.
        var tries = 0;
        _mainloopTimer.SetTimer(() =>
        {
            if (arena.Status < ArenaState.Running) return ++tries < 300;
            if (arena.Status == ArenaState.Running)
            {
                arena.GetCallback<ArenaActionCallback.ArenaActionDelegate>()?.Invoke(arena, ArenaAction.Create);
                Log(LogLevel.Info, $"{{{arena.Name}}} GameMode: started {type} (replayed the arena's Create).");
            }
            return false;
        }, 0, 100, arena);
    }

    /// <summary>A failed attach or detach, corrected on the mainloop (where _gameModeAttached lives).</summary>
    private void Settle(Arena arena, string type, bool attaching) =>
        _mainloop.QueueMainWorkItem(s =>
        {
            if (_gameModeAttached.TryGetValue(s.arena, out var ours)) GameModeAttach.Settle(ours, s.type, s.attaching, ok: false);
        }, (arena, type, attaching));
}
