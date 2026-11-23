using Dare.Arenas.Core;
using Xunit;

namespace Dare.Arenas.Tests;

public class WebAuthTests
{
    private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private WebAuth Make() => new(() => _now);

    [Fact]
    public void Token_redeems_once()
    {
        var w = Make();
        var token = w.IssueLoginToken("Alice");
        var session = w.Redeem(token);
        Assert.NotNull(session);
        Assert.Equal("Alice", w.PlayerFor(session!));
        Assert.Null(w.Redeem(token));
    }

    [Fact]
    public void Token_expires_after_ten_minutes()
    {
        var w = Make();
        var token = w.IssueLoginToken("Alice");
        _now = _now.AddMinutes(10).AddSeconds(1);
        Assert.Null(w.Redeem(token));
    }

    [Fact]
    public void Token_valid_just_before_ten_minutes()
    {
        var w = Make();
        var token = w.IssueLoginToken("Alice");
        _now = _now.AddMinutes(9).AddSeconds(59);
        Assert.NotNull(w.Redeem(token));
    }

    [Fact]
    public void Session_valid_24h_then_null()
    {
        var w = Make();
        var session = w.Redeem(w.IssueLoginToken("Alice"))!;
        _now = _now.AddHours(23).AddMinutes(59);
        Assert.Equal("Alice", w.PlayerFor(session));
        _now = _now.AddMinutes(2);
        Assert.Null(w.PlayerFor(session));
    }

    [Fact]
    public void Unknown_values_return_null()
    {
        var w = Make();
        Assert.Null(w.Redeem("nope"));
        Assert.Null(w.PlayerFor("nope"));
    }

    [Fact]
    public void Tokens_differ_and_are_urlsafe_32_bytes()
    {
        var w = Make();
        var a = w.IssueLoginToken("Alice");
        var b = w.IssueLoginToken("Alice");
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }
}
