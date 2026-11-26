using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class GameLoginTests
{
    [Fact]
    public void ok_is_not_refused() => Assert.Null(GameLogin.Refusal(LoginResult.Ok, continuum: true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void wrong_password_is_bad_password_for_every_client(bool continuum) =>
        Assert.Equal((GameRefusal.BadPassword, (string?)null), GameLogin.Refusal(LoginResult.BadPassword, continuum));

    [Fact]
    public void continuum_gets_the_custom_texts()
    {
        Assert.Equal((GameRefusal.CustomText, "This name isn't claimed. Claim it at subspacefoundation.com (email optional), or log in as guest."),
            GameLogin.Refusal(LoginResult.UnknownName, continuum: true));
        Assert.Equal((GameRefusal.CustomText, "Too many wrong passwords. Try again in 10 minutes."),
            GameLogin.Refusal(LoginResult.LockedOut, continuum: true));
        Assert.Equal(GameRefusal.CustomText, GameLogin.Refusal(LoginResult.Disabled, continuum: true)!.Value.Kind);
        Assert.Equal(GameRefusal.CustomText, GameLogin.Refusal(null, continuum: true)!.Value.Kind);
    }

    [Theory]
    [InlineData(LoginResult.UnknownName)]
    [InlineData(LoginResult.LockedOut)]
    [InlineData(LoginResult.Disabled)]
    [InlineData(null)]
    public void vie_clients_get_no_permission_without_text(LoginResult? result) =>
        Assert.Equal((GameRefusal.NoPermission, (string?)null), GameLogin.Refusal(result, continuum: false));
}
