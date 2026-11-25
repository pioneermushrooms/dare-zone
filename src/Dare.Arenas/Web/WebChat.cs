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
