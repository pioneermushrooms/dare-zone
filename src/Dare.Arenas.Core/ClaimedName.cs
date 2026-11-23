namespace Dare.Arenas.Core;

/// <summary>
/// Whether a player name is claimed: it has a real password hash in passwd.conf <c>[Users]</c>
/// (not <c>any</c> or <c>lock</c>). With RequireAuthenticationToSetPassword = no, whoever first
/// types <c>?passwd</c> on an unclaimed name owns it, so roles (co-mod, transfer) only go to
/// claimed names or to players who are online and authenticated.
/// </summary>
public static class ClaimedName
{
    private const string UsersSection = "users";

    /// <summary>True for a password hash; false for nothing, <c>any</c> (any password, not authenticated) and <c>lock</c>.</summary>
    public static bool IsClaim(string? value)
    {
        var v = value?.Trim();
        return !string.IsNullOrEmpty(v)
               && !v.Equals("any", StringComparison.OrdinalIgnoreCase)
               && !v.Equals("lock", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The spelling of <paramref name="name"/> as stored in passwd.conf's <c>[Users]</c> section (or a
    /// <c>users:Name = …</c> override line), if it is claimed there; else null. Last definition wins,
    /// as in the server's conf reader.
    /// </summary>
    public static string? Canonical(string passwdText, string name)
    {
        var wanted = name?.Trim() ?? "";
        if (wanted.Length == 0 || string.IsNullOrEmpty(passwdText)) return null;

        string? found = null;
        var section = "";
        foreach (var raw in passwdText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '/') continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                section = end > 0 ? line[1..end].Trim() : "";
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            var keySection = section;
            var colon = key.IndexOf(':');
            if (colon >= 0)
            {
                keySection = key[..colon].Trim();
                key = key[(colon + 1)..].Trim();
            }

            if (!keySection.Equals(UsersSection, StringComparison.OrdinalIgnoreCase)
                || !key.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            found = IsClaim(value) ? key : null;
        }
        return found;
    }

    /// <summary>
    /// The name to give a role to, or null when <paramref name="typed"/> is not claimed.
    /// <paramref name="onlineAuthenticated"/>: the name of the matching online player if authenticated.
    /// <paramref name="passwdValue"/>: the server's current <c>[Users]</c> value for the name (in memory).
    /// <paramref name="passwdText"/>: passwd.conf on disk, used only for the stored spelling.
    /// </summary>
    public static string? Resolve(string typed, string? onlineAuthenticated, string? passwdValue, string? passwdText)
    {
        if (!string.IsNullOrWhiteSpace(onlineAuthenticated)) return onlineAuthenticated;
        if (!IsClaim(passwdValue)) return null;
        var trimmed = typed.Trim();
        return (passwdText is null ? null : Canonical(passwdText, trimmed)) ?? trimmed;
    }
}
