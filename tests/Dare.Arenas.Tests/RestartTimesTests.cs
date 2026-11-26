using Dare.Arenas.Core;
using Xunit;

namespace Dare.Arenas.Tests;

public class RestartTimesTests
{
    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(40, "40 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(89, "1 minute")]
    [InlineData(90, "2 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(3600 + 5 * 60, "1 hour 5 minutes")]
    [InlineData(86400, "1 day")]
    [InlineData(2 * 86400 + 3600, "2 days 1 hour")]
    [InlineData(-5, "0 seconds")]
    public void Until_words(int seconds, string expected) =>
        Assert.Equal(expected, RestartTimes.Until(TimeSpan.FromSeconds(seconds)));
}
