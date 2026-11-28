using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// King of the Hill paid as written (GameMode:KingReward). The server's Koth pays (players)^2 * RewardFactor / 1000 and
// has no flat option, so a game mode that wants a flat reward sets King:RewardFactor 0 (Koth then pays nothing) and
// GameMode:KingReward; when Koth's game ends the plugin pays its winners (KingWin in Core), as Koth would: the jackpot
// added, split if King:SplitPoints, flag points and a KOTH win in the stats, and the same arena message.
// A staff ?resetkoth also ends Koth's game, and the crowned pilots are paid then too. Mainloop only.
public sealed partial class ArenaOwnership
{
    private readonly Dictionary<Arena, int> _kingRewards = new();
    private readonly Dictionary<Player, DateTime> _crownLost = new();
    private IAllPlayerStats? _allPlayerStats;
    private IScoreStats? _scoreStats;

    /// <summary>How recently a crown must have run out for its pilot to count among winners whose crowns expired together.</summary>
    private static readonly TimeSpan CrownsTogether = TimeSpan.FromSeconds(1);

    private void StartKingReward(IComponentBroker broker)
    {
        _allPlayerStats = broker.GetInterface<IAllPlayerStats>();
        _scoreStats = broker.GetInterface<IScoreStats>();
        CrownToggledCallback.Register(broker, Callback_KingCrownToggled);
        KothEndedCallback.Register(broker, Callback_KingEnded);
    }

    private void StopKingReward(IComponentBroker broker)
    {
        CrownToggledCallback.Unregister(broker, Callback_KingCrownToggled);
        KothEndedCallback.Unregister(broker, Callback_KingEnded);
        if (_allPlayerStats is not null) broker.ReleaseInterface(ref _allPlayerStats);
        if (_scoreStats is not null) broker.ReleaseInterface(ref _scoreStats);
        _kingRewards.Clear();
        _crownLost.Clear();
    }

    [ConfigHelp<int>("GameMode", "KingReward", ConfigScope.Arena, Default = 0, Min = 0, Max = 1000000,
        Description = "Points for winning King of the Hill, as written (0 = the server's King:RewardFactor formula). Set King:RewardFactor to 0 with it, or winners are paid twice.")]
    private void ReadKingReward(Arena arena, ConfigHandle cfg)
    {
        var points = Math.Max(0, _configManager.GetInt(cfg, "GameMode", "KingReward", 0));
        if (points > 0) _kingRewards[arena] = points; else _kingRewards.Remove(arena);
    }

    private void Callback_KingCrownToggled(Player player, bool on)
    {
        if (on || player.Arena is not { } arena || !_kingRewards.ContainsKey(arena)) { _crownLost.Remove(player); return; }
        _crownLost[player] = DateTime.UtcNow;
    }

    private void Callback_KingEnded(Arena arena)
    {
        if (!_kingRewards.TryGetValue(arena, out var reward) || arena.Cfg is not { } cfg) return;
        var now = DateTime.UtcNow;
        var crowned = new List<(Player, short)>();
        var justLost = new List<(Player, short)>();
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
            {
                if (p.Arena != arena || p.Status != PlayerState.Playing || p.Ship == ShipType.Spec) continue;
                if (p.Packet.HasCrown) crowned.Add((p, p.Freq));
                else if (_crownLost.TryGetValue(p, out var at) && now - at <= CrownsTogether) justLost.Add((p, p.Freq));
            }
        }
        finally { _playerData.Unlock(); }
        foreach (var stale in _crownLost.Where(kv => now - kv.Value > CrownsTogether || kv.Key.Arena == arena).Select(kv => kv.Key).ToList())
            _crownLost.Remove(stale);

        var winners = KingWin.Winners(crowned, justLost);
        if (winners.Count == 0) return; // Koth has said "no winner"
        var points = reward + Jackpot(arena);
        if (_configManager.GetInt(cfg, "King", "SplitPoints", 1) != 0) points /= winners.Count;
        var sound = ChatSound.Ding;
        foreach (var p in winners)
        {
            _allPlayerStats?.IncrementStat(p, StatCodes.FlagPoints, null, points);
            _allPlayerStats?.IncrementStat(p, StatCodes.KothGamesWon, null, 1);
            _chat.SendArenaMessage(arena, sound, $"King of the Hill: {p.Name} awarded {points} points");
            sound = ChatSound.None;
        }
        _scoreStats?.SendUpdates(arena, null);
        Log(LogLevel.Info, $"{{{arena.Name}}} GameMode: King of the Hill paid {points} points to {winners.Count} pilot(s).");
    }
}
