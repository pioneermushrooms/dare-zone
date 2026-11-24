using System.Net;
using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

/// <summary>Core helpers behind the /account web pages: sessions, cookies, client IP, origin, secrets, hour limits.</summary>
public sealed class AccountWebTests
{
    private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    // ---- sessions ----------------------------------------------------------

    [Fact]
    public void account_session_maps_to_name_and_account()
    {
        var w = new WebAuth(() => _now);
        var s = w.IssueSession("Big Chris", 7);
        Assert.Equal("Big Chris", w.PlayerFor(s));
        Assert.Equal(("Big Chris", (long?)7), w.SessionFor(s));
    }

    [Fact]
    public void weblogin_session_has_no_account()
    {
        var w = new WebAuth(() => _now);
        var s = w.Redeem(w.IssueLoginToken("Alice"))!;
        Assert.Equal(("Alice", (long?)null), w.SessionFor(s));
    }

    [Fact]
    public void revoke_ends_the_session()
    {
        var w = new WebAuth(() => _now);
        var s = w.IssueSession("Big Chris", 7);
        w.Revoke(s);
        Assert.Null(w.PlayerFor(s));
        Assert.Null(w.SessionFor(s));
        w.Revoke("unknown"); // harmless
    }

    [Fact]
    public void revoke_all_for_a_name_keeps_the_exception()
    {
        var w = new WebAuth(() => _now);
        var keep = w.IssueSession("Big Chris", 7);
        var other = w.IssueSession("big chris", 7);
        var game = w.Redeem(w.IssueLoginToken("BIG CHRIS"))!;
        var someoneElse = w.IssueSession("Alice", 8);
        w.RevokeAllFor("Big Chris", except: keep);
        Assert.NotNull(w.PlayerFor(keep));
        Assert.Null(w.PlayerFor(other));
        Assert.Null(w.PlayerFor(game));
        Assert.NotNull(w.PlayerFor(someoneElse));
    }

    [Fact]
    public void account_session_expires_after_24h()
    {
        var w = new WebAuth(() => _now);
        var s = w.IssueSession("Big Chris", 7);
        _now = _now.AddHours(24).AddSeconds(1);
        Assert.Null(w.SessionFor(s));
    }

    // ---- cookies -------------------------------------------------------------

    [Fact]
    public void session_cookie_is_site_wide()
    {
        Assert.Equal("dare_session=abc; Path=/; Max-Age=86400; HttpOnly; Secure; SameSite=Lax", WebPaths.SessionCookie("abc", secure: true));
    }

    [Fact]
    public void clear_cookie_expires_it()
    {
        Assert.Equal("dare_session=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax", WebPaths.ClearSessionCookie());
    }

    // ---- client IP -----------------------------------------------------------

    [Fact]
    public void forwarded_for_trusted_only_from_loopback()
    {
        Assert.Equal("203.0.113.9", WebPaths.ClientIp(IPAddress.Loopback, "203.0.113.9, 10.0.0.1"));
        Assert.Equal("203.0.113.9", WebPaths.ClientIp(IPAddress.Parse("::ffff:127.0.0.1"), " 203.0.113.9"));
        Assert.Equal("198.51.100.4", WebPaths.ClientIp(IPAddress.Parse("198.51.100.4"), "203.0.113.9"));
        Assert.Equal("127.0.0.1", WebPaths.ClientIp(IPAddress.Loopback, null));
        Assert.Equal("127.0.0.1", WebPaths.ClientIp(IPAddress.Loopback, "garbage"));
        Assert.Equal("2001:db8::1", WebPaths.ClientIp(IPAddress.IPv6Loopback, "2001:db8::1"));
        Assert.Equal("unknown", WebPaths.ClientIp(null, "203.0.113.9"));
    }

    // ---- origin --------------------------------------------------------------

    [Theory]
    [InlineData("https://subspacefoundation.com", true)]
    [InlineData("https://SubspaceFoundation.com", true)]
    [InlineData("https://subspacefoundation.com/", false)]
    [InlineData("http://subspacefoundation.com", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("https://subspacefoundation.com.evil.example", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void origin_must_match_the_public_site(string? origin, bool ok)
    {
        Assert.Equal(ok, WebPaths.IsAllowedOrigin(origin, "https://subspacefoundation.com/arena"));
    }

    [Fact]
    public void origin_with_port()
    {
        Assert.True(WebPaths.IsAllowedOrigin("http://127.0.0.1:8088", "http://127.0.0.1:8088/arena"));
        Assert.False(WebPaths.IsAllowedOrigin("http://127.0.0.1:9999", "http://127.0.0.1:8088/arena"));
    }

    // ---- secrets file --------------------------------------------------------

    [Fact]
    public void secrets_reads_section_key()
    {
        var text = "; comment\n[Other]\nApiKey = nope\n\n[Resend]\n  ApiKey =  re_123abc  \nFrom = X <a@b.c>\n";
        Assert.Equal("re_123abc", SecretsFile.Get(text, "Resend", "ApiKey"));
        Assert.Equal("X <a@b.c>", SecretsFile.Get(text, "resend", "from"));
        Assert.Null(SecretsFile.Get(text, "Resend", "Missing"));
        Assert.Null(SecretsFile.Get("ApiKey = top\n", "Resend", "ApiKey"));
        Assert.Null(SecretsFile.Get("[Resend]\nApiKey =\n", "Resend", "ApiKey"));
    }

    // ---- hour windows --------------------------------------------------------

    [Fact]
    public void rate_limiter_with_an_hour_window()
    {
        var r = new RateLimiter(5, TimeSpan.FromHours(1), () => _now);
        for (var i = 0; i < 5; i++) { Assert.True(r.TryTake("e")); _now = _now.AddMinutes(2); }
        Assert.False(r.TryTake("e"));
        _now = _now.AddMinutes(51); // first hit is now 61 minutes old
        Assert.True(r.TryTake("e"));
        Assert.False(r.TryTake("e"));
    }
}
