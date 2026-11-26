using System.Security.Cryptography;
using System.Text;

namespace Dare.Arenas.Core;

/// <summary>
/// Where site sessions live. <see cref="AccountStore"/> keeps them in the database, so a sign-in
/// survives zone restarts; <see cref="MemoryWebSessions"/> is the fallback (tests, store closed).
/// Keys are hashes of the session ids (<see cref="WebAuth.HashSession"/>), never the ids. Thread-safe.
/// </summary>
public interface IWebSessionStore
{
    void SaveSession(string hash, string player, long? accountId, DateTimeOffset expires);
    /// <summary>The live session (expires after <paramref name="now"/>), or null.</summary>
    (string Player, long? AccountId, DateTimeOffset Expires)? FindSession(string hash, DateTimeOffset now);
    void ExtendSession(string hash, DateTimeOffset expires);
    void DeleteSession(string hash);
    /// <summary>Deletes every session of <paramref name="player"/> (any case) but <paramref name="exceptHash"/>.</summary>
    void DeleteSessionsFor(string player, string? exceptHash);
    void PruneSessions(DateTimeOffset now);
}

/// <summary>In-memory sessions: lost on restart. Used only when no persistent store is attached.</summary>
public sealed class MemoryWebSessions : IWebSessionStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Player, long? AccountId, DateTimeOffset Expires)> _s = new();

    public void SaveSession(string hash, string player, long? accountId, DateTimeOffset expires)
    {
        lock (_lock) _s[hash] = (player, accountId, expires);
    }

    public (string Player, long? AccountId, DateTimeOffset Expires)? FindSession(string hash, DateTimeOffset now)
    {
        lock (_lock) return _s.TryGetValue(hash, out var e) && e.Expires > now ? e : null;
    }

    public void ExtendSession(string hash, DateTimeOffset expires)
    {
        lock (_lock) if (_s.TryGetValue(hash, out var e)) _s[hash] = e with { Expires = expires };
    }

    public void DeleteSession(string hash)
    {
        lock (_lock) _s.Remove(hash);
    }

    public void DeleteSessionsFor(string player, string? exceptHash)
    {
        lock (_lock)
            foreach (var k in _s.Where(p => p.Key != exceptHash && string.Equals(p.Value.Player, player, StringComparison.OrdinalIgnoreCase))
                                .Select(p => p.Key).ToList())
                _s.Remove(k);
    }

    public void PruneSessions(DateTimeOffset now)
    {
        lock (_lock)
            foreach (var k in _s.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _s.Remove(k);
    }
}

/// <summary>
/// Single-use sign-in tokens (10 min, in memory) redeemed into site sessions. A session lasts
/// <see cref="SessionLifetime"/> from its last use (renewed at most once per
/// <see cref="RenewEvery"/>), and lives in the attached <see cref="IWebSessionStore"/> so zone
/// restarts don't sign pilots out. Sign-out, password change and reset revoke sessions. Thread-safe.
/// </summary>
public sealed class WebAuth
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromSeconds(WebPaths.SessionSeconds);
    public static readonly TimeSpan RenewEvery = TimeSpan.FromDays(1);

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Player, DateTimeOffset Expires)> _tokens = new();
    private readonly IWebSessionStore _memory = new MemoryWebSessions();
    private volatile IWebSessionStore? _store;

    public WebAuth(Func<DateTimeOffset> clock, IWebSessionStore? store = null)
    {
        _clock = clock;
        _store = store;
    }

    /// <summary>Keeps sessions in <paramref name="store"/> from now on (null: back to memory only).</summary>
    public void Attach(IWebSessionStore? store) => _store = store;

    private IWebSessionStore Sessions => _store ?? _memory;

    public string IssueLoginToken(string player)
    {
        lock (_lock)
        {
            var now = _clock();
            foreach (var k in _tokens.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _tokens.Remove(k);
            var token = NewId();
            _tokens[token] = (player, now + TokenLifetime);
            return token;
        }
    }

    /// <summary>Consumes a token; returns a new session id, or null if unknown, used or expired.</summary>
    public string? Redeem(string token)
    {
        string player;
        lock (_lock)
        {
            if (token is null || !_tokens.Remove(token, out var entry) || entry.Expires <= _clock()) return null;
            player = entry.Player;
        }
        return NewSession(player, null);
    }

    /// <summary>A session for a site sign-in with an account name and password.</summary>
    public string IssueSession(string player, long accountId) => NewSession(player, accountId);

    private string NewSession(string player, long? accountId)
    {
        var now = _clock();
        var s = Sessions;
        s.PruneSessions(now);
        var session = NewId();
        s.SaveSession(HashSession(session), player, accountId, now + SessionLifetime);
        return session;
    }

    public string? PlayerFor(string session) => SessionFor(session)?.Player;

    /// <summary>The session's name, and its account id when it came from an account sign-in (null for ?weblogin).</summary>
    public (string Player, long? AccountId)? SessionFor(string session)
    {
        if (string.IsNullOrEmpty(session)) return null;
        return Sessions.FindSession(HashSession(session), _clock()) is { } e ? (e.Player, e.AccountId) : null;
    }

    /// <summary>
    /// Slides a live session's expiry to a full <see cref="SessionLifetime"/> from now, when it was
    /// last renewed over <see cref="RenewEvery"/> ago. True when renewed: the caller re-sends the cookie.
    /// </summary>
    public bool Renew(string session)
    {
        if (string.IsNullOrEmpty(session)) return false;
        var now = _clock();
        var s = Sessions;
        var hash = HashSession(session);
        if (s.FindSession(hash, now) is not { } e) return false;
        if (e.Expires > now + SessionLifetime - RenewEvery) return false;
        s.ExtendSession(hash, now + SessionLifetime);
        return true;
    }

    /// <summary>Ends a session (sign-out).</summary>
    public void Revoke(string session)
    {
        if (!string.IsNullOrEmpty(session)) Sessions.DeleteSession(HashSession(session));
    }

    /// <summary>Ends every session of <paramref name="player"/> (any case) but <paramref name="except"/>, e.g. after a password change.</summary>
    public void RevokeAllFor(string player, string? except = null) =>
        Sessions.DeleteSessionsFor(player, string.IsNullOrEmpty(except) ? null : HashSession(except));

    /// <summary>What the store keeps for a session id: a database leak gives no usable sessions.</summary>
    public static string HashSession(string session) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("web-session:" + session))).ToLowerInvariant();

    private static string NewId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
