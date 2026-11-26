using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class WebPathsTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/arena", "/")]
    [InlineData("/arena/", "/")]
    [InlineData("/arena/api/me", "/api/me")]
    [InlineData("/api/me", "/api/me")]
    [InlineData("/login", "/login")]
    [InlineData("/arena/login", "/login")]
    [InlineData("/arenas/x", "/arenas/x")]          // only the exact /arena segment is stripped
    [InlineData("/arena/arena.html", "/arena.html")]
    [InlineData("/arena.html", "/arena.html")]
    [InlineData("/ARENA/api/me", "/ARENA/api/me")]  // case-sensitive, as Caddy's matcher
    public void strips_an_optional_arena_prefix(string raw, string expected)
    {
        Assert.Equal(expected, WebPaths.Normalize(raw));
    }

    [Fact]
    public void session_cookie_is_persistent_30_days_httponly_secure_lax_site_wide()
    {
        var c = WebPaths.SessionCookie("abc", secure: true);
        Assert.Equal("dare_session=abc; Path=/; Max-Age=2592000; HttpOnly; Secure; SameSite=Lax", c);
        Assert.DoesNotContain("Expires", c); // Max-Age makes it persistent, not a browser-session cookie
    }

    [Fact]
    public void session_cookie_without_secure_only_when_asked()
    {
        var c = WebPaths.SessionCookie("abc", secure: false);
        Assert.DoesNotContain("Secure", c);
        Assert.Contains("HttpOnly", c);
        Assert.Contains("SameSite=Lax", c);
    }
}
