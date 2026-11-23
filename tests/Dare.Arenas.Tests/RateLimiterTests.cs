using Dare.Arenas.Core;
using Xunit;

namespace Dare.Arenas.Tests;

public class RateLimiterTests
{
    private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Allows_20_then_refuses_21st_then_allows_after_a_minute()
    {
        var r = new RateLimiter(20, () => _now);
        for (var i = 0; i < 20; i++) Assert.True(r.TryTake("a"));
        Assert.False(r.TryTake("a"));
        _now = _now.AddSeconds(61);
        Assert.True(r.TryTake("a"));
    }

    [Fact]
    public void Keys_are_independent()
    {
        var r = new RateLimiter(1, () => _now);
        Assert.True(r.TryTake("a"));
        Assert.False(r.TryTake("a"));
        Assert.True(r.TryTake("b"));
    }

    [Fact]
    public void Window_slides()
    {
        var r = new RateLimiter(2, () => _now);
        Assert.True(r.TryTake("a"));
        _now = _now.AddSeconds(30);
        Assert.True(r.TryTake("a"));
        Assert.False(r.TryTake("a"));
        _now = _now.AddSeconds(31);
        Assert.True(r.TryTake("a"));
        Assert.False(r.TryTake("a"));
    }
}
