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
    private readonly Dictionary<string, (string Player, DateTimeOffset Expires)> _sessions = new();

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
            _sessions[session] = (entry.Player, now + SessionLifetime);
            return session;
        }
    }

    public string? PlayerFor(string session)
    {
        lock (_lock)
        {
            Prune(_clock());
            return session is not null && _sessions.TryGetValue(session, out var e) ? e.Player : null;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var k in _tokens.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _tokens.Remove(k);
        foreach (var k in _sessions.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _sessions.Remove(k);
    }

    private static string NewId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
