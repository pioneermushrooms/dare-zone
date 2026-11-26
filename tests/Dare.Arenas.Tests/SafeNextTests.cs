using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

// Where a pilot goes after signing in: only same-site paths, never another host.
public sealed class SafeNextTests
{
    [Theory]
    [InlineData("/arena/me")]
    [InlineData("/arena/map?arena=Big%20Rock")]
    [InlineData("/account/")]
    [InlineData("/account/#pilots")]
    [InlineData("/arena/staff")]
    [InlineData("/")]
    [InlineData("/arena/map?arena=x&tab=tiles#top")]
    public void same_site_paths_are_kept(string next)
    {
        Assert.Equal(next, WebPaths.SafeNext(next));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example/")]
    [InlineData("///evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("\\\\evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("http:/evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("evil.example")]
    [InlineData("arena/me")]
    [InlineData("/%2Fevil.example")]
    [InlineData("/%5Cevil.example")]
    [InlineData("/arena/me\r\nSet-Cookie: x=1")]
    [InlineData("/arena/ me")]
    [InlineData("/arena/\tme")]
    [InlineData("/account/login")]
    [InlineData("/account/login?next=/arena/me")]
    [InlineData("/Account/Signup")]
    [InlineData("/account/verify")]
    [InlineData("/account/api/logout")]
    [InlineData("/arena/login?t=abc")]
    public void anything_else_is_refused(string? next)
    {
        Assert.Null(WebPaths.SafeNext(next));
    }

    [Fact]
    public void too_long_is_refused()
    {
        Assert.Null(WebPaths.SafeNext("/" + new string('a', 600)));
    }

    [Fact]
    public void a_cookie_value_encoded_once_is_accepted()
    {
        Assert.Equal("/arena/map?arena=x", WebPaths.SafeNext(Uri.EscapeDataString("/arena/map?arena=x")));
        Assert.Null(WebPaths.SafeNext(Uri.EscapeDataString("//evil.example/")));
    }

    [Fact]
    public void after_sign_in_prefers_the_parameter_then_the_cookie_then_the_default()
    {
        Assert.Equal("/arena/map", WebPaths.AfterSignIn("/arena/map", "%2Farena%2Fstaff", "/account/"));
        Assert.Equal("/arena/staff", WebPaths.AfterSignIn(null, "%2Farena%2Fstaff", "/account/"));
        Assert.Equal("/arena/staff", WebPaths.AfterSignIn("//evil.example", "%2Farena%2Fstaff", "/account/"));
        Assert.Equal("/account/", WebPaths.AfterSignIn("https://evil.example", "//evil", "/account/"));
        Assert.Equal("/arena/me", WebPaths.AfterSignIn(null, null, "/arena/me"));
    }
}
