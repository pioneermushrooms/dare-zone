namespace Dare.Arenas.Core;

/// <summary>Request-path and cookie helpers for the arena web pages.</summary>
public static class WebPaths
{
    public const string Prefix = "/arena";
    public const string SessionCookieName = "dare_arena_session";
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
    /// Set-Cookie value for a session: HttpOnly, SameSite=Lax, 24 h, scoped to <c>/arena</c>;
    /// Secure unless <paramref name="secure"/> is false (local plain-http testing only).
    /// </summary>
    public static string SessionCookie(string session, bool secure) =>
        $"{SessionCookieName}={session}; Path={Prefix}; Max-Age={SessionSeconds}; HttpOnly"
        + (secure ? "; Secure" : "") + "; SameSite=Lax";
}
