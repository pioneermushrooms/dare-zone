using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// The home page's chat panel. Anyone sees each public arena's recent public chat and who is in
// it; a signed-in pilot can press Chat to join the arena as a chat client (ChatBridge) and talk.
// Drafts (#name) are private: their chat is only seen from a bridge inside them.
public sealed partial class ArenaOwnership
{
    private const int FeedMax = 100;
    private const int MaxBridges = 64;
    private static readonly TimeSpan BridgeIdle = TimeSpan.FromSeconds(45);

    private readonly object _chatLock = new();
    private readonly Dictionary<string, List<ChatLine>> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, ChatBridge> _bridges = new();
    private long _feedSeq;
    private Timer? _bridgeSweep;

    private void LoadWebChat(IComponentBroker broker)
    {
        ChatMessageCallback.Register(broker, Callback_Chat);
        _bridgeSweep = new Timer(_ => SweepBridges(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private void UnloadWebChat(IComponentBroker broker)
    {
        ChatMessageCallback.Unregister(broker, Callback_Chat);
        _bridgeSweep?.Dispose();
        _bridgeSweep = null;
        List<ChatBridge> all;
        lock (_chatLock) { all = _bridges.Values.ToList(); _bridges.Clear(); }
        foreach (var b in all) b.Dispose();
    }

    /// <summary>Mainloop: keeps each public arena's public lines (no commands, no team or private chat).</summary>
    private void Callback_Chat(Arena? arena, Player? player, ChatMessageType type, ChatSound sound, Player? toPlayer, short freq, ReadOnlySpan<char> message)
    {
        if (arena is null || DraftRules.IsDraft(arena.Name)) return;
        if (type is not (ChatMessageType.Pub or ChatMessageType.PubMacro or ChatMessageType.Arena)) return;
        if (type != ChatMessageType.Arena && player is null) return;
        if (message.IsEmpty || message[0] is '?' or '*') return;
        if (type == ChatMessageType.Pub && player is not null) AddressBot(arena, player, message);
        var line = new ChatLine(0, DateTimeOffset.UtcNow, type == ChatMessageType.Arena ? "ARENA" : "PUB",
            type == ChatMessageType.Arena ? null : player!.Name, message.ToString());
        lock (_chatLock)
        {
            if (!_feeds.TryGetValue(arena.Name, out var feed)) _feeds[arena.Name] = feed = new List<ChatLine>();
            feed.Add(line with { Id = ++_feedSeq });
            if (feed.Count > FeedMax) feed.RemoveRange(0, feed.Count - FeedMax);
        }
    }

    /// <summary>The zone's name for the arena the page picked ("" is the public arena, "0").</summary>
    internal static string? ChatArenaName(string? picked)
    {
        var name = (picked ?? "").Trim();
        if (name.Length == 0 || name == "0") return "0";
        if (name.Length > 16 || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '#') || name.LastIndexOf('#') > 0) return null;
        return name.ToLowerInvariant();
    }

    /// <summary>The public feed of <paramref name="arena"/> after <paramref name="after"/>.</summary>
    internal (IReadOnlyList<ChatLine> Lines, long Last) PublicChat(string arena, long after)
    {
        lock (_chatLock)
        {
            var feed = _feeds.TryGetValue(arena, out var f) ? f : new List<ChatLine>();
            return (feed.Where(l => l.Id > after).ToList(), _feedSeq);
        }
    }

    /// <summary>Mainloop: who is in <paramref name="arena"/> (name, ship 1-8 or 0 for spectating, team).</summary>
    internal IReadOnlyList<object> ChatPlayers(string arena)
    {
        var list = new List<(string Name, int Ship, short Freq)>();
        _playerData.Lock();
        try
        {
            foreach (var p in _playerData.Players)
                if (p.Status == PlayerState.Playing && p.Arena is not null && string.Equals(p.Arena.Name, arena, StringComparison.OrdinalIgnoreCase) && p.Name is not null)
                    list.Add((p.Name, p.Ship == ShipType.Spec ? 0 : (int)p.Ship + 1, p.Freq));
        }
        finally
        {
            _playerData.Unlock();
        }
        return list.OrderBy(p => p.Freq).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => (object)new { name = p.Name, ship = p.Ship, freq = p.Freq }).ToList();
    }

    internal ChatBridge? BridgeFor(long accountId)
    {
        lock (_chatLock)
        {
            if (!_bridges.TryGetValue(accountId, out var b)) return null;
            b.LastSeen = DateTimeOffset.UtcNow;
            return b;
        }
    }

    /// <summary>
    /// Mainloop: the pilot signed in as <paramref name="account"/> joins <paramref name="arena"/> as a
    /// chat client (replacing any chat they had open). Refused while that name is in the zone already.
    /// </summary>
    internal Result StartChat(Account account, string arena)
    {
        if (_playerData.FindPlayer(account.Name) is { } inZone && inZone.ClientName?.StartsWith("chat: foundation-web", StringComparison.Ordinal) != true)
            return new Result(false, $"{account.Name} is in the zone already. Chat from the game, or leave it first.");
        if (DraftRules.IsDraft(arena) && Store.Draft(DraftRules.PublicOf(arena)) is null)
            return new Result(false, "No such arena.");
        StopChat(account.Id);
        lock (_chatLock)
            if (_bridges.Count >= MaxBridges)
                return new Result(false, "Web chat is full right now; try again in a minute.");
        var pass = Accounts.IssueGamePass(account.Id);
        if (pass is null) return new Result(false, "This account can't sign in.");
        var port = _configManager.GetInt(_configManager.Global, "Listen", "Port", 5000);
        var bridge = new ChatBridge(account.Id, account.Name, arena);
        lock (_chatLock) _bridges[account.Id] = bridge;
        _ = bridge.RunAsync("127.0.0.1", port, pass);
        Log(LogLevel.Info, $"Web chat: {account.Name} joining [{arena}].");
        return new Result(true, "Joining…");
    }

    internal void StopChat(long accountId)
    {
        ChatBridge? b;
        lock (_chatLock) { if (_bridges.Remove(accountId, out b) is false) return; }
        b?.Dispose();
    }

    private void SweepBridges()
    {
        var now = DateTimeOffset.UtcNow;
        List<ChatBridge> stale;
        lock (_chatLock)
        {
            stale = _bridges.Values.Where(b => b.State == "closed" ? now - b.LastSeen > TimeSpan.FromSeconds(15) : now - b.LastSeen > BridgeIdle).ToList();
            foreach (var b in stale) _bridges.Remove(b.AccountId);
        }
        foreach (var b in stale) b.Dispose();
    }
}

// The home page's arena list: what the game's ?arena -a shows (every arena the zone has, with
// how many are in each), minus private and suspended ones.
public sealed partial class ArenaOwnership
{
    /// <summary>Mainloop: [{name, players}], "0" being the public arena; busiest first, then by name.</summary>
    internal IReadOnlyList<object> ZoneArenas()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _arenaManager.Lock();
        try
        {
            foreach (var known in _arenaManager.KnownArenaNames)
            {
                var name = known.Span.ToString();
                if (name.Equals("(public)", StringComparison.OrdinalIgnoreCase)) name = "0";
                counts.TryAdd(name, 0);
            }
            _arenaManager.GetPopulationSummary(out _, out _);
            foreach (var arena in _arenaManager.Arenas)
            {
                if (arena.Status != ArenaState.Running) continue;
                arena.GetPlayerCounts(out var total, out _);
                counts[arena.Name] = total;
            }
        }
        finally
        {
            _arenaManager.Unlock();
        }
        var hidden = Store.Arenas().Where(a => a.Status != ArenaStatus.Active).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        counts.TryAdd("0", 0);
        return counts
            .Where(p => p.Key.Length > 0 && p.Key[0] is not ('#' or '_') && !hidden.Contains(p.Key) && !IsHiddenArena(p.Key))
            .OrderByDescending(p => p.Key == "0").ThenByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => (object)new { name = p.Key, players = p.Value })
            .ToList();
    }

    /// <summary>An arena its owner switched to private: left out of the site's lists.</summary>
    private bool IsHiddenArena(string name) => Store.IsPrivate(name);
}

// Owner actions from the arena page: public/private, and discard.
public sealed partial class ArenaOwnership
{
    /// <summary>Mainloop: {"arena", "private": true|false}. The caller checked the owner role.</summary>
    internal Result WebSetPrivate(string player, string arena, System.Text.Json.JsonElement body)
    {
        var makePrivate = body.TryGetProperty("private", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
        var r = Store.SetPrivate(arena, player, makePrivate);
        if (r.Ok) Log(LogLevel.Info, $"(web) {player} made [{arena}] {(makePrivate ? "private" : "public")}.");
        return r;
    }

    /// <summary>Mainloop: {"arena", "confirm": the arena's name}. Archives it and sends everyone out.</summary>
    internal Result WebDiscard(string player, string arena, System.Text.Json.JsonElement body)
    {
        var confirm = body.TryGetProperty("confirm", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() : null;
        if (!string.Equals((confirm ?? "").Trim(), arena, StringComparison.OrdinalIgnoreCase))
            return new Result(false, $"Type {arena} to confirm.");
        var r = Store.Discard(arena, player);
        if (!r.Ok) return r;
        SendPlayersOut(arena, $"Arena '{arena}' was discarded by its owner.");
        Log(LogLevel.Info, $"(web) {player} discarded [{arena}].");
        return new Result(true, $"Discarded '{arena}'. Staff can bring it back if you change your mind.");
    }
}
