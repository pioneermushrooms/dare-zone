using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class WebLoginGuardTests
{
    private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private WebLoginGuard Guard() => new(() => _now);

    [Fact]
    public void ten_failures_for_one_name_and_ip_lock_it_for_ten_minutes()
    {
        var g = Guard();
        for (var i = 0; i < 9; i++) g.Failure("Big Chris", "1.2.3.4");
        Assert.False(g.IsLocked("Big Chris", "1.2.3.4"));
        g.Failure("Big Chris", "1.2.3.4");
        Assert.True(g.IsLocked("big chris", "1.2.3.4"));
        _now = _now.AddMinutes(9);
        Assert.True(g.IsLocked("Big Chris", "1.2.3.4"));
        _now = _now.AddMinutes(1).AddSeconds(1);
        Assert.False(g.IsLocked("Big Chris", "1.2.3.4"));
    }

    [Fact]
    public void another_ip_or_another_name_is_not_affected()
    {
        var g = Guard();
        for (var i = 0; i < 10; i++) g.Failure("Big Chris", "1.2.3.4");
        Assert.True(g.IsLocked("Big Chris", "1.2.3.4"));
        Assert.False(g.IsLocked("Big Chris", "5.6.7.8"));
        Assert.False(g.IsLocked("Someone", "1.2.3.4"));
    }

    [Fact]
    public void unknown_names_lock_the_same_way_so_the_lock_tells_nothing()
    {
        var g = Guard();
        for (var i = 0; i < 10; i++) g.Failure("nobody-here", "1.2.3.4");
        Assert.True(g.IsLocked("nobody-here", "1.2.3.4"));
    }

    [Fact]
    public void failures_outside_the_ten_minute_window_do_not_add_up()
    {
        var g = Guard();
        for (var i = 0; i < 9; i++) g.Failure("Big Chris", "1.2.3.4");
        _now = _now.AddMinutes(11);
        g.Failure("Big Chris", "1.2.3.4");
        Assert.False(g.IsLocked("Big Chris", "1.2.3.4"));
    }

    [Fact]
    public void a_success_clears_the_count()
    {
        var g = Guard();
        for (var i = 0; i < 9; i++) g.Failure("Big Chris", "1.2.3.4");
        g.Success("Big Chris", "1.2.3.4");
        g.Failure("Big Chris", "1.2.3.4");
        Assert.False(g.IsLocked("Big Chris", "1.2.3.4"));
    }
}
