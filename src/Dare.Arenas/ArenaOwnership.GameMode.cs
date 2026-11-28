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
    }

    private void StopGameMode(IComponentBroker broker)
    {
        if (_moduleManager is null) return;
        ArenaActionCallback.Unregister(broker, Callback_ArenaActionGameMode);
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
            return;
        }
        if (action is ArenaAction.Create or ArenaAction.ConfChanged) ApplyGameModeAttach(arena);
    }

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
        _ = ChangeModulesAsync(modules, arena, attach, detach);
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
            }
            catch (Exception ex) { Log(LogLevel.Error, $"{{{arena.Name}}} GameMode: attaching {t} failed: {ex.Message}"); Settle(arena, t, attaching: true); }
        }
    }

    /// <summary>A failed attach or detach, corrected on the mainloop (where _gameModeAttached lives).</summary>
    private void Settle(Arena arena, string type, bool attaching) =>
        _mainloop.QueueMainWorkItem(s =>
        {
            if (_gameModeAttached.TryGetValue(s.arena, out var ours)) GameModeAttach.Settle(ours, s.type, s.attaching, ok: false);
        }, (arena, type, attaching));
}
