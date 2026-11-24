using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class GameLoginTests
{
    [Fact]
    public void ok_is_not_refused() => Assert.Null(GameLogin.Refusal(LoginResult.Ok, continuum: true));

    [Fact]
    public void continuum_gets_plain_text_with_the_next_step()
    {
        Assert.Equal((GameRefusal.CustomText, "This name isn't registered. Register at subspacefoundation.com/account/signup"),
            GameLogin.Refusal(LoginResult.UnknownName, continuum: true));
        Assert.Equal((GameRefusal.CustomText, "Wrong password. Forgot it? subspacefoundation.com/account/forgot"),
            GameLogin.Refusal(LoginResult.BadPassword, continuum: true));
        Assert.Equal((GameRefusal.CustomText, "Too many tries. Wait 10 minutes, then try again."),
            GameLogin.Refusal(LoginResult.LockedOut, continuum: true));
        Assert.Equal(GameRefusal.CustomText, GameLogin.Refusal(LoginResult.Disabled, continuum: true)!.Value.Kind);
        Assert.Equal(GameRefusal.CustomText, GameLogin.Refusal(null, continuum: true)!.Value.Kind);
    }

    [Fact]
    public void lockout_text_matches_the_real_lockout_length() =>
        Assert.Contains($"{(int)AccountStore.LockoutDuration.TotalMinutes} minutes", GameLogin.LockedOutText);

    [Fact]
    public void custom_texts_fit_continuums_limit_and_the_legacy_code_page()
    {
        foreach (var t in new[] { GameLogin.UnknownNameText, GameLogin.BadPasswordText, GameLogin.LockedOutText,
                     GameLogin.DisabledText, GameLogin.ErrorText })
        {
            Assert.True(t.Length < 250, t);
            Assert.All(t, c => Assert.True(c < 128, $"non-ASCII in '{t}'"));
        }
    }

    [Theory]
    [InlineData(LoginResult.BadPassword, GameRefusal.BadPassword)]
    [InlineData(LoginResult.UnknownName, GameRefusal.NoPermission)]
    [InlineData(LoginResult.LockedOut, GameRefusal.LockedOut)]
    [InlineData(LoginResult.Disabled, GameRefusal.Disabled)]
    [InlineData(null, GameRefusal.Busy)]
    public void vie_clients_get_a_distinct_code_and_no_text(LoginResult? result, GameRefusal kind) =>
        Assert.Equal((kind, (string?)null), GameLogin.Refusal(result, continuum: false));
}
