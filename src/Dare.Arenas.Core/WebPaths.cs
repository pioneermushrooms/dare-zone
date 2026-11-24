using System.Net;

namespace Dare.Arenas.Core;

/// <summary>Request-path and cookie helpers for the arena web pages.</summary>
public static class WebPaths
{
    public const string Prefix = "/arena";
    // One cookie for the whole site (Path=/), so /account and /arena share the session.
    public const string SessionCookieName = "dare_session";
    public const int SessionSeconds = 24 * 60 * 60;

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
    /// Set-Cookie value for a session: HttpOnly, SameSite=Lax, 24 h, site-wide (<c>Path=/</c>);
    /// Secure unless <paramref name="secure"/> is false (local plain-http testing only).
    /// </summary>
    public static string SessionCookie(string session, bool secure) =>
        $"{SessionCookieName}={session}; Path=/; Max-Age={SessionSeconds}; HttpOnly"
        + (secure ? "; Secure" : "") + "; SameSite=Lax";

    /// <summary>Set-Cookie value that removes the session cookie (sign-out).</summary>
    public static string ClearSessionCookie() => $"{SessionCookieName}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax";

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
