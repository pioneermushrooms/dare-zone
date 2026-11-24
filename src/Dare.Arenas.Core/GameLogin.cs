namespace Dare.Arenas.Core;

/// <summary>How the zone answers a refused game login (the plugin maps this to the server's AuthCode).</summary>
public enum GameRefusal { BadPassword, NoPermission, CustomText, LockedOut, Disabled, Busy }

/// <summary>
/// Game login answers. Continuum can show a custom text; VIE clients (and bots, chat clients) only
/// understand the standard codes, so each failure gets a code of its own and the Dare client words
/// it: unregistered name = NoPermission, wrong password = BadPassword, lockout = LockedOut,
/// disabled = NoPermission2, internal error = ServerBusy.
/// </summary>
public static class GameLogin
{
    public const string UnknownNameText = "This name isn't registered. Register at subspacefoundation.com/account/signup";
    public const string BadPasswordText = "Wrong password. Forgot it? subspacefoundation.com/account/forgot";
    public const string LockedOutText = "Too many tries. Wait 10 minutes, then try again.";
    public const string DisabledText = "This account is disabled.";
    public const string ErrorText = "Login is unavailable right now. Try again soon.";

    /// <summary>The refusal for a failed <see cref="LoginResult"/> (null for <see cref="LoginResult.Ok"/>); null result = internal error.</summary>
    public static (GameRefusal Kind, string? Text)? Refusal(LoginResult? result, bool continuum)
    {
        if (result == LoginResult.Ok) return null;
        if (continuum)
        {
            return result switch
            {
                LoginResult.BadPassword => (GameRefusal.CustomText, BadPasswordText),
                LoginResult.UnknownName => (GameRefusal.CustomText, UnknownNameText),
                LoginResult.LockedOut => (GameRefusal.CustomText, LockedOutText),
                LoginResult.Disabled => (GameRefusal.CustomText, DisabledText),
                _ => (GameRefusal.CustomText, ErrorText),
            };
        }
        return result switch
        {
            LoginResult.BadPassword => (GameRefusal.BadPassword, null),
            LoginResult.UnknownName => (GameRefusal.NoPermission, null),
            LoginResult.LockedOut => (GameRefusal.LockedOut, null),
            LoginResult.Disabled => (GameRefusal.Disabled, null),
            _ => (GameRefusal.Busy, null),
        };
    }
}
