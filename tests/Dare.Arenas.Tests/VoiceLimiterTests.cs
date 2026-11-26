using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class VoiceLimiterTests
{
    private DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Three_in_six_seconds_then_the_fourth_silences_for_ten()
    {
        var l = new VoiceLimiter(() => _now);
        for (var i = 0; i < 3; i++) Assert.Equal(VoiceVerdictKind.Allowed, l.Try("a", VoiceLimits.Default).Kind);
        Assert.Equal(new VoiceVerdict(VoiceVerdictKind.SilencedNow, 10), l.Try("a", VoiceLimits.Default));
        _now = _now.AddSeconds(4);
        Assert.Equal(new VoiceVerdict(VoiceVerdictKind.Silenced, 6), l.Try("a", VoiceLimits.Default));
        _now = _now.AddSeconds(6.1);
        Assert.Equal(VoiceVerdictKind.Allowed, l.Try("a", VoiceLimits.Default).Kind);
    }

    [Fact]
    public void Spaced_out_lines_never_silence()
    {
        var l = new VoiceLimiter(() => _now);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(VoiceVerdictKind.Allowed, l.Try("a", VoiceLimits.Default).Kind);
            _now = _now.AddSeconds(2.1);
        }
    }

    [Fact]
    public void Attempts_while_silenced_do_not_extend_it()
    {
        var l = new VoiceLimiter(() => _now);
        for (var i = 0; i < 3; i++) l.Try("a", VoiceLimits.Default);
        Assert.Equal(VoiceVerdictKind.SilencedNow, l.Try("a", VoiceLimits.Default).Kind);
        for (var s = 1; s < 10; s++)
        {
            _now = _now.AddSeconds(1);
            Assert.Equal(VoiceVerdictKind.Silenced, l.Try("a", VoiceLimits.Default).Kind);
        }
        _now = _now.AddSeconds(1); // exactly 10 s after the trip
        Assert.Equal(VoiceVerdictKind.Allowed, l.Try("a", VoiceLimits.Default).Kind);
    }

    [Fact]
    public void Players_are_independent_and_limits_are_per_call()
    {
        var l = new VoiceLimiter(() => _now);
        for (var i = 0; i < 4; i++) l.Try("a", VoiceLimits.Default);
        Assert.Equal(VoiceVerdictKind.Allowed, l.Try("b", VoiceLimits.Default).Kind);
        var one = new VoiceLimits(1, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(5));
        Assert.Equal(VoiceVerdictKind.Allowed, l.Try("c", one).Kind);
        Assert.Equal(new VoiceVerdict(VoiceVerdictKind.SilencedNow, 5), l.Try("c", one));
    }

    [Fact]
    public void Seconds_left_rounds_up()
    {
        var l = new VoiceLimiter(() => _now);
        for (var i = 0; i < 4; i++) l.Try("a", VoiceLimits.Default);
        _now = _now.AddSeconds(0.8);
        Assert.Equal(new VoiceVerdict(VoiceVerdictKind.Silenced, 10), l.Try("a", VoiceLimits.Default));
    }
}
