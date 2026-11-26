using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class BotPassBookTests
{
    // a stand-in for the zone's pending passes: Issue replaces, a login (Redeem) takes one once
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private int _serial;
    private readonly BotPassBook _book;
    private readonly DateTimeOffset _t0 = DateTimeOffset.UnixEpoch;

    public BotPassBookTests() => _book = new BotPassBook(
        name => _pending[name] = "pass" + ++_serial,
        (name, pass) => _pending.TryGetValue(name, out var p) && p == pass,
        name => _pending.Remove(name));

    private bool Login(string name, string pass) =>
        _pending.TryGetValue(name, out var p) && p == pass && _pending.Remove(name);

    [Fact]
    public void a_new_bot_gets_a_pass_and_keeps_it_while_unused()
    {
        var pass = _book.PassFor("Rex", inZone: false, _t0);
        Assert.Equal("pass1", pass);
        Assert.True(_book.Holds("rex"));
        Assert.Equal(pass, _book.PassFor("Rex", false, _t0.AddSeconds(14)));
    }

    [Fact]
    public void an_unused_pass_is_replaced_after_its_lifetime()
    {
        var first = _book.PassFor("Rex", false, _t0)!;
        var second = _book.PassFor("Rex", false, _t0 + BotPassBook.Lifetime);
        Assert.NotEqual(first, second);
        Assert.False(Login("Rex", first));
        Assert.True(Login("Rex", second!));
    }

    [Fact]
    public void the_next_pass_is_issued_as_soon_as_one_is_used_so_a_restart_can_log_in()
    {
        var first = _book.PassFor("Rex", false, _t0)!;
        Assert.True(Login("Rex", first));
        // the bot is in on it: the next lineup already carries an unspent pass
        var next = _book.PassFor("Rex", inZone: true, _t0.AddSeconds(1));
        Assert.NotEqual(first, next);
        // a host restart (any edit) logs the bot in again at once, on the new pass
        Assert.True(Login("Rex", next!));
        var third = _book.PassFor("Rex", true, _t0.AddSeconds(2));
        Assert.NotEqual(next, third);
        // and while the bot stays in, its unspent pass stays the same (the lineup text does not change)
        Assert.Equal(third, _book.PassFor("Rex", true, _t0.AddMinutes(10)));
    }

    [Fact]
    public void a_bot_in_the_zone_without_a_pass_from_this_book_is_not_listed()
    {
        Assert.Null(_book.PassFor("Old", inZone: true, _t0));
        Assert.False(_book.Holds("Old"));
        Assert.NotNull(_book.PassFor("Old", inZone: false, _t0.AddSeconds(1)));
    }

    [Fact]
    public void bots_no_longer_listed_lose_their_pass()
    {
        var rex = _book.PassFor("Rex", false, _t0)!;
        var ida = _book.PassFor("Ida", false, _t0)!;
        _book.KeepOnly(["REX"]);
        Assert.True(_book.Holds("Rex"));
        Assert.False(_book.Holds("Ida"));
        Assert.False(Login("Ida", ida));
        _book.Clear();
        Assert.False(_book.Holds("Rex"));
        Assert.False(Login("Rex", rex));
    }
}
