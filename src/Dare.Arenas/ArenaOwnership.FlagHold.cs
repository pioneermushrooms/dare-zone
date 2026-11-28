using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;

namespace Dare.Arenas;

// The flag win and the round clock for game-mode arenas (GameMode:Attach has Flags; GameMode:RoundTime).
// The server's FlagGamePoints wins the moment a team has every flag and never reads Flag:WinDelay, and a flag
// win left Misc:TimedGame running. So the plugin runs the flag rule itself (FlagHold in Core):
//  - a team that has every flag starts a WinDelay countdown, announced; losing a flag cancels it; the win (the
//    server's own flag reset with points, as FlagGamePoints gives) comes when it ends;
//  - GameMode:RoundTime is a round the plugin runs through IGameTimer, so a flag win can restart it; when time
//    runs out, the team with the most flags wins, and a tie is a draw (flags reset, no points).
// Mainloop only.
public sealed partial class ArenaOwnership
{
    private sealed class FlagRule
    {
        public readonly FlagHoldClock Clock = new();
        public FlagWinMode Mode;
        public double RewardRatio;
        public bool SplitPoints;
        public bool Music;
        public bool MusicOn;
    }

    private readonly Dictionary<Arena, FlagRule> _flagRules = new();
    private readonly Dictionary<Arena, TimeSpan> _rounds = new();
    private IGameTimer? _gameTimer;

    private void StartFlagHold(IComponentBroker broker)
    {
        _gameTimer = broker.GetInterface<IGameTimer>();
        GameTimerEndedCallback.Register(broker, Callback_RoundEnded);
        _mainloopTimer.SetTimer(TickFlagHold, 1000, 1000, null);
    }

    private void StopFlagHold(IComponentBroker broker)
    {
        _mainloopTimer.ClearTimer(TickFlagHold, null);
        GameTimerEndedCallback.Unregister(broker, Callback_RoundEnded);
        foreach (var arena in _flagRules.Keys.ToList()) StopFlagRule(arena);
        _rounds.Clear();
        if (_gameTimer is not null) broker.ReleaseInterface(ref _gameTimer);
    }

    [ConfigHelp<int>("GameMode", "RoundTime", ConfigScope.Arena, Default = 0, Min = 0, Max = 8640000,
        Description = "How long a round lasts (1/100 s; 0 = no rounds). Unlike Misc:TimedGame, a flag win starts the next round, and when time runs out the team with the most flags wins (a tie is a draw).")]
    private void ApplyRound(Arena arena, ConfigHandle cfg, bool created)
    {
        var length = TimeSpan.FromMilliseconds(Math.Max(0, _configManager.GetInt(cfg, "GameMode", "RoundTime", 0)) * 10L);
        var had = _rounds.GetValueOrDefault(arena);
        if (length <= TimeSpan.Zero) _rounds.Remove(arena); else _rounds[arena] = length;
        if (_gameTimer is null || (!created && had == length)) return;
        if (length <= TimeSpan.Zero && had <= TimeSpan.Zero) return;
        if (!_gameTimer.SetTimer(arena, length))
            Log(LogLevel.Warn, $"{{{arena.Name}}} GameMode:RoundTime ignored: Misc:TimedGame is set, and it runs its own clock.");
    }

    // ---- the flag rule, attached through GameMode:Attach ------------------------------------------------

    private void StartFlagRule(Arena arena)
    {
        if (_flagRules.ContainsKey(arena)) return;
        var rule = new FlagRule();
        _flagRules[arena] = rule;
        ReadFlagRule(arena, rule);
        FlagGainCallback.Register(arena, Callback_FlagHoldGain);
        FlagLostCallback.Register(arena, Callback_FlagHoldLost);
        FlagOnMapCallback.Register(arena, Callback_FlagHoldOnMap);
        FlagGameResetCallback.Register(arena, Callback_FlagHoldReset);
        Log(LogLevel.Info, $"{{{arena.Name}}} GameMode: flag rule on ({rule.Mode}).");
    }

    private void StopFlagRule(Arena arena)
    {
        if (!_flagRules.Remove(arena, out var rule)) return;
        FlagGainCallback.Unregister(arena, Callback_FlagHoldGain);
        FlagLostCallback.Unregister(arena, Callback_FlagHoldLost);
        FlagOnMapCallback.Unregister(arena, Callback_FlagHoldOnMap);
        FlagGameResetCallback.Unregister(arena, Callback_FlagHoldReset);
        if (rule.MusicOn) _chat.SendArenaMessage(arena, ChatSound.MusicStop, "");
        Log(LogLevel.Info, $"{{{arena.Name}}} GameMode: flag rule off.");
    }

    private void ReadFlagRule(Arena arena, FlagRule rule)
    {
        if (arena.Cfg is not { } cfg) return;
        rule.Mode = FlagHold.ParseMode(_configManager.GetStr(cfg, "Flag", "FlagMode"));
        rule.RewardRatio = _configManager.GetInt(cfg, "Flag", "FlagReward", 5000) / 1000.0;
        rule.SplitPoints = _configManager.GetInt(cfg, "Flag", "SplitPoints", 0) != 0;
        rule.Music = _configManager.GetInt(cfg, "Misc", "VictoryMusic", 1) != 0;
    }

    // The flag's state is settled once the callback returns, so look then.
    private void Callback_FlagHoldGain(Arena arena, Player player, short flagId, FlagPickupReason reason) => QueueFlagCheck(arena);
    private void Callback_FlagHoldLost(Arena arena, Player player, short flagId, FlagLostReason reason) => QueueFlagCheck(arena);
    private void Callback_FlagHoldOnMap(Arena arena, short flagId, TileCoordinates coordinates, short freq) => QueueFlagCheck(arena);

    private void Callback_FlagHoldReset(Arena arena, short winnerFreq, int points)
    {
        if (!_flagRules.TryGetValue(arena, out var rule)) return;
        rule.Clock.Reset();
        StopMusic(arena, rule);
    }

    private void QueueFlagCheck(Arena arena) => _mainloop.QueueMainWorkItem(CheckFlagHold, arena);

    private bool TickFlagHold()
    {
        foreach (var arena in _flagRules.Keys.ToList()) CheckFlagHold(arena);
        return true;
    }

    private void CheckFlagHold(Arena arena)
    {
        if (!_flagRules.TryGetValue(arena, out var rule)) return;
        var flags = ReadFlags(arena, out var delay);
        if (flags is null) return;
        var e = rule.Clock.Update(FlagHold.Holder(rule.Mode, flags), DateTime.UtcNow, delay);
        switch (e.Step)
        {
            case FlagHoldStep.Started:
                _chat.SendArenaMessage(arena, $"Freq {e.Freq} has every flag! They win in {FlagHold.Say(e.Remaining)} unless another team takes one.");
                if (rule.Music && !rule.MusicOn) { rule.MusicOn = true; _chat.SendArenaMessage(arena, ChatSound.MusicLoop, ""); }
                break;
            case FlagHoldStep.Warn:
                _chat.SendArenaMessage(arena, $"Freq {e.Freq} wins in {FlagHold.Say(e.Remaining)} unless another team takes a flag.");
                break;
            case FlagHoldStep.Cancelled:
                _chat.SendArenaMessage(arena, $"Freq {e.Freq} lost a flag: no win yet.");
                StopMusic(arena, rule);
                break;
            case FlagHoldStep.Won:
                FlagWin(arena, rule, e.Freq, restartRound: true);
                break;
        }
    }

    /// <summary>Every flag's spot and team, and the arena's Flag:WinDelay; null when the arena has no carry-flag game.</summary>
    private static List<FlagView>? ReadFlags(Arena arena, out TimeSpan winDelay)
    {
        winDelay = TimeSpan.Zero;
        var game = arena.GetInterface<ICarryFlagGame>();
        if (game is null) return null;
        try
        {
            winDelay = game.GetSettings(arena)?.WinDelay ?? TimeSpan.Zero;
            var count = game.GetFlagCount(arena);
            var list = new List<FlagView>(count);
            for (short i = 0; i < count; i++)
            {
                if (!game.TryGetFlagInfo(arena, i, out var info)) { list.Add(new(FlagSpot.None, -1)); continue; }
                var spot = info.State switch { FlagState.Carried => FlagSpot.Carried, FlagState.OnMap => FlagSpot.OnMap, _ => FlagSpot.None };
                list.Add(new(spot, info.Freq));
            }
            return list;
        }
        finally { arena.ReleaseInterface(ref game); }
    }

    private void StopMusic(Arena arena, FlagRule rule)
    {
        if (!rule.MusicOn) return;
        rule.MusicOn = false;
        _chat.SendArenaMessage(arena, ChatSound.MusicStop, "");
    }

    /// <summary>The win as the server's FlagGamePoints gives it: (players)^2 * FlagReward/1000 plus the jackpot, split if asked.</summary>
    private void FlagWin(Arena arena, FlagRule rule, short freq, bool restartRound)
    {
        int playing = 0, onFreq = 0;
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
            {
                if (p.Arena != arena || p.Status != PlayerState.Playing || p.Ship == ShipType.Spec) continue;
                playing++;
                if (p.Freq == freq) onFreq++;
            }
        }
        finally { _playerData.Unlock(); }

        var points = (int)(playing * playing * rule.RewardRatio);
        var jackpot = arena.GetInterface<IJackpot>();
        if (jackpot is not null) { try { points += jackpot.GetJackpot(arena); } finally { arena.ReleaseInterface(ref jackpot); } }
        if (onFreq > 0 && rule.SplitPoints) points /= onFreq;

        StopMusic(arena, rule);
        rule.Clock.Reset();
        var game = arena.GetInterface<ICarryFlagGame>();
        if (game is not null) { try { game.ResetGame(arena, freq, points, true); } finally { arena.ReleaseInterface(ref game); } }
        EndGameInterval(arena);
        if (restartRound && _rounds.TryGetValue(arena, out var round) && _gameTimer?.SetTimer(arena, round) == true)
            _chat.SendArenaMessage(arena, $"Next round: {FlagHold.Say(round)}.");
    }

    private static void EndGameInterval(Arena arena)
    {
        var persist = arena.GetInterface<IPersistExecutor>();
        if (persist is null) return;
        try { persist.EndInterval(PersistInterval.Game, arena); }
        finally { arena.ReleaseInterface(ref persist); }
    }

    // ---- when the round's time runs out ---------------------------------------------------------------

    private void Callback_RoundEnded(Arena arena)
    {
        if (!_rounds.TryGetValue(arena, out var round)) return; // Misc:TimedGame or ?timer: the server's own clock
        if (_flagRules.TryGetValue(arena, out var rule) && ReadFlags(arena, out _) is { } flags)
        {
            if (FlagHold.MostFlags(flags) is { } winner)
            {
                _chat.SendArenaMessage(arena, $"Time's up: Freq {winner} has the most flags and wins.");
                FlagWin(arena, rule, winner, restartRound: false);
            }
            else
            {
                _chat.SendArenaMessage(arena, "Time's up: no team has the most flags, so it's a draw.");
                StopMusic(arena, rule);
                rule.Clock.Reset();
                var game = arena.GetInterface<ICarryFlagGame>();
                if (game is not null) { try { game.ResetGame(arena, -1, 0, true); } finally { arena.ReleaseInterface(ref game); } }
                EndGameInterval(arena);
            }
        }
        // The timer stops itself after this callback (it isn't a Misc:TimedGame), so start the next round after.
        _mainloop.QueueMainWorkItem(s =>
        {
            if (_rounds.TryGetValue(s.arena, out var r) && r == s.round && _gameTimer?.SetTimer(s.arena, r) == true)
                _chat.SendArenaMessage(s.arena, $"Next round: {FlagHold.Say(r)}.");
        }, (arena, round));
    }
}
