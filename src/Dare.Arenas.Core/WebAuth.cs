using System.Security.Cryptography;

namespace Dare.Arenas.Core;

/// <summary>Single-use sign-in tokens (10 min) redeemed into sessions (24 h). Thread-safe.</summary>
public sealed class WebAuth
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Player, DateTimeOffset Expires)> _tokens = new();
    private readonly Dictionary<string, (string Player, long? AccountId, DateTimeOffset Expires)> _sessions = new();

    public WebAuth(Func<DateTimeOffset> clock) => _clock = clock;

    public string IssueLoginToken(string player)
    {
        lock (_lock)
        {
            var now = _clock();
            Prune(now);
            var token = NewId();
            _tokens[token] = (player, now + TokenLifetime);
            return token;
        }
    }

    /// <summary>Consumes a token; returns a new session id, or null if unknown, used or expired.</summary>
    public string? Redeem(string token)
    {
        lock (_lock)
        {
            var now = _clock();
            Prune(now);
            if (token is null || !_tokens.Remove(token, out var entry)) return null;
            var session = NewId();
            _sessions[session] = (entry.Player, null, now + SessionLifetime);
            return session;
        }
    }

    /// <summary>A session for a site sign-in with an account name and password (24 h).</summary>
    public string IssueSession(string player, long accountId)
    {
        lock (_lock)
        {
            var now = _clock();
            Prune(now);
            var session = NewId();
            _sessions[session] = (player, accountId, now + SessionLifetime);
            return session;
        }
    }

    public string? PlayerFor(string session) => SessionFor(session)?.Player;

    /// <summary>The session's name, and its account id when it came from an account sign-in (null for ?weblogin).</summary>
    public (string Player, long? AccountId)? SessionFor(string session)
    {
        lock (_lock)
        {
            Prune(_clock());
            return session is not null && _sessions.TryGetValue(session, out var e) ? (e.Player, e.AccountId) : null;
        }
    }

    /// <summary>Ends a session (sign-out).</summary>
    public void Revoke(string session)
    {
        lock (_lock)
            if (session is not null) _sessions.Remove(session);
    }

    /// <summary>Ends every session of <paramref name="player"/> (any case) but <paramref name="except"/>, e.g. after a password change.</summary>
    public void RevokeAllFor(string player, string? except = null)
    {
        lock (_lock)
            foreach (var k in _sessions.Where(p => p.Key != except && string.Equals(p.Value.Player, player, StringComparison.OrdinalIgnoreCase))
                                       .Select(p => p.Key).ToList())
                _sessions.Remove(k);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var k in _tokens.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _tokens.Remove(k);
        foreach (var k in _sessions.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _sessions.Remove(k);
    }

    private static string NewId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
