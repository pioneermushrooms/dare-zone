using System.Net;

namespace Dare.Arenas.Core;

/// <summary>Request-path and cookie helpers for the arena web pages.</summary>
public static class WebPaths
{
    public const string Prefix = "/arena";
    // One cookie for the whole site (Path=/), so /account and /arena share the session.
    public const string SessionCookieName = "dare_session";
    /// <summary>A site sign-in lasts 30 days from its last use (the server renews it, see WebAuth.Renew).</summary>
    public const int SessionSeconds = 30 * 24 * 60 * 60;
    /// <summary>Where a pilot was going before signing in (set by the pages; read once by the sign-in).</summary>
    public const string NextCookieName = "dare_next";
    /// <summary>Where a sign-in on the account pages goes when no valid <c>next</c> is given.</summary>
    public const string DefaultAfterSignIn = "/account/";
    private const int MaxNextLength = 512;

    // Sign-in pages themselves: going "back" to one would loop.
    private static readonly string[] NoReturnTo =
        ["/account/login", "/account/signup", "/account/verify", "/account/forgot", "/account/reset", "/account/api", Prefix + "/login"];

    /// <summary>
    /// The route for a request path, with an optional leading <c>/arena</c> segment removed:
    /// production traffic arrives through Caddy's <c>handle_path /arena/*</c> (prefix stripped),
    /// local testing uses <c>http://127.0.0.1:8088/arena/...</c>. Always starts with '/'.
    /// </summary>
    public static string Normalize(string? path)
    {
        var p = string.IsNullOrEmpty(path) ? "/" : path;
        if (p[0] != '/') p = "/" + p;
        if (p == Prefix) return "/";
        if (p.StartsWith(Prefix + "/", StringComparison.Ordinal)) p = p[Prefix.Length..];
        return p;
    }

    /// <summary>
    /// Set-Cookie value for a session: HttpOnly, SameSite=Lax, persistent (30 days), site-wide (<c>Path=/</c>);
    /// Secure unless <paramref name="secure"/> is false (local plain-http testing only).
    /// </summary>
    public static string SessionCookie(string session, bool secure) =>
        $"{SessionCookieName}={session}; Path=/; Max-Age={SessionSeconds}; HttpOnly"
        + (secure ? "; Secure" : "") + "; SameSite=Lax";

    /// <summary>Set-Cookie value that removes the session cookie (sign-out).</summary>
    public static string ClearSessionCookie() => $"{SessionCookieName}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax";

    /// <summary>Set-Cookie value that removes the return-to cookie once a sign-in has used it.</summary>
    public static string ClearNextCookie() => $"{NextCookieName}=; Path=/; Max-Age=0; Secure; SameSite=Lax";

    /// <summary>
    /// <paramref name="next"/> when it is a safe place on this site to go after signing in, else null:
    /// a path starting with a single '/' (not <c>//host</c> or <c>/\host</c>), no scheme, no
    /// backslash, no control characters or spaces, at most 512 characters, and not a sign-in page.
    /// A query and fragment are kept. Accepts the value percent-encoded once (as a cookie holds it).
    /// </summary>
    public static string? SafeNext(string? next)
    {
        if (string.IsNullOrEmpty(next)) return null;
        if (next.Length > MaxNextLength * 3) return null;
        var n = next;
        if (n.StartsWith("%2F", StringComparison.OrdinalIgnoreCase))
        {
            try { n = Uri.UnescapeDataString(n); } catch { return null; }
        }
        if (n.Length is 0 or > MaxNextLength) return null;
        if (n[0] != '/' || (n.Length > 1 && (n[1] == '/' || n[1] == '\\'))) return null;
        foreach (var ch in n)
            if (ch <= ' ' || ch == '\\' || ch == 0x7f || char.IsControl(ch) || char.IsWhiteSpace(ch)) return null;
        // the path part, decoded, must not turn into //host or /\host either
        var pathEnd = n.IndexOfAny(['?', '#']);
        var path = pathEnd < 0 ? n : n[..pathEnd];
        string decoded;
        try { decoded = Uri.UnescapeDataString(path); } catch { return null; }
        if (decoded.Length > 1 && (decoded[1] == '/' || decoded[1] == '\\')) return null;
        if (decoded.Contains('\\')) return null;
        if (!Uri.TryCreate("https://site.invalid" + n, UriKind.Absolute, out var u) || u.Host != "site.invalid") return null;
        var lower = decoded.ToLowerInvariant();
        foreach (var p in NoReturnTo)
            if (lower == p || lower.StartsWith(p + "/", StringComparison.Ordinal) || lower.StartsWith(p + "?", StringComparison.Ordinal)) return null;
        return n;
    }

    /// <summary>
    /// The place to go after signing in: <paramref name="asked"/> (a <c>next</c> parameter) when safe,
    /// else the <see cref="NextCookieName"/> cookie when safe, else <paramref name="fallback"/>.
    /// </summary>
    public static string AfterSignIn(string? asked, string? cookie, string fallback) =>
        SafeNext(asked) ?? SafeNext(cookie) ?? fallback;

    /// <summary>
    /// The client's address for per-IP limits. <c>X-Forwarded-For</c> (set by Caddy) is trusted only
    /// when the connection itself comes from loopback; then its first address is used.
    /// </summary>
    public static string ClientIp(IPAddress? remote, string? forwardedFor)
    {
        if (remote is null) return "unknown";
        var r = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        if (IPAddress.IsLoopback(r) && !string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var fwd))
                return (fwd.IsIPv4MappedToIPv6 ? fwd.MapToIPv4() : fwd).ToString();
        }
        return r.ToString();
    }

    /// <summary>
    /// True when an <c>Origin</c> header names exactly the scheme, host and port of the public site
    /// (taken from <paramref name="publicBaseUrl"/>, e.g. <c>https://subspacefoundation.com/arena</c>).
    /// </summary>
    public static bool IsAllowedOrigin(string? origin, string publicBaseUrl)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var site)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        if (o.AbsolutePath != "/" || origin.EndsWith('/') || o.Query.Length > 0 || o.UserInfo.Length > 0) return false;
        return string.Equals(o.Scheme, site.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(o.Host, site.Host, StringComparison.OrdinalIgnoreCase)
               && o.Port == site.Port;
    }
}
