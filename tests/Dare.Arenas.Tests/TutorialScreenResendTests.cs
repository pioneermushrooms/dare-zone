using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class TutorialScreenResendTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2.9, 0, false)]
    [InlineData(3, 0, true)]
    [InlineData(7, 1, false)]
    [InlineData(8, 1, true)]
    [InlineData(60, 2, false)]
    public void Due_FollowsTheDelays(double seconds, int done, bool expected) =>
        Assert.Equal(expected, TutorialScreenResend.Due(TimeSpan.FromSeconds(seconds), done));
}
