namespace Dare.Arenas.Core;

/// <summary>How the zone answers a refused game login (the plugin maps this to the server's AuthCode).</summary>
public enum GameRefusal { BadPassword, NoPermission, CustomText }

/// <summary>
/// Game login answers. Continuum can show a custom text; VIE clients (and bots, chat clients) only
/// understand the standard codes, so they get <see cref="GameRefusal.NoPermission"/> instead.
/// </summary>
public static class GameLogin
{
    public const string UnknownNameText = "This name isn't claimed. Claim it at subspacefoundation.com (email optional), or log in as guest.";
    public const string LockedOutText = "Too many wrong passwords. Try again in 10 minutes.";
    public const string DisabledText = "This account is disabled.";
    public const string ErrorText = "Login is unavailable right now. Try again soon.";

    /// <summary>The refusal for a failed <see cref="LoginResult"/> (null for <see cref="LoginResult.Ok"/>); null result = internal error.</summary>
    public static (GameRefusal Kind, string? Text)? Refusal(LoginResult? result, bool continuum)
    {
        return result switch
        {
            LoginResult.Ok => null,
            LoginResult.BadPassword => (GameRefusal.BadPassword, null),
            LoginResult.UnknownName => Text(UnknownNameText),
            LoginResult.LockedOut => Text(LockedOutText),
            LoginResult.Disabled => Text(DisabledText),
            _ => Text(ErrorText),
        };

        (GameRefusal, string?) Text(string text) =>
            continuum ? (GameRefusal.CustomText, text) : (GameRefusal.NoPermission, null);
    }
}
