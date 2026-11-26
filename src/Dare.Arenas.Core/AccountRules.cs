namespace Dare.Arenas.Core;

/// <summary>Validation rules for account names, passwords and recovery emails.</summary>
public static class AccountRules
{
    private const int NameMin = 3, NameMax = 20, PasswordMin = 8, PasswordMax = 31, EmailMax = 254;

    /// <param name="name">The input unchanged (original casing) on success, empty on failure.</param>
    public static bool TryName(string input, out string name, out string error)
    {
        name = "";
        input ??= "";
        if (input.Length < NameMin || input.Length > NameMax)
            return Fail($"Names are {NameMin}-{NameMax} characters.", out error);
        if (!IsAsciiLetter(input[0]))
            return Fail("Names must start with a letter.", out error);
        if (input[^1] == ' ' || input.Contains("  "))
            return Fail("Names can't end in a space or have double spaces.", out error);
        foreach (var c in input)
        {
            if (!(IsAsciiLetter(c) || (c >= '0' && c <= '9') || c is ' ' or '_' or '-' or '.'))
                return Fail("Names can only use letters, digits, spaces, _ - and .", out error);
        }
        name = input;
        error = "";
        return true;
    }

    /// <summary>The name typed in the game to fly as a guest (any password); the zone picks the real name.</summary>
    public const string GuestLogin = "guest";

    /// <summary>True for "guest" itself and "Guest" followed by digits (the names guests get): nobody can claim them.</summary>
    public static bool IsGuestName(string? input)
    {
        var s = (input ?? "").Trim();
        if (!s.StartsWith("guest", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = s.AsSpan(5);
        foreach (var c in rest) if (c < '0' || c > '9') return false;
        return true;
    }

    /// <summary>A name for a new account: <see cref="TryName"/> plus the names kept for guests.</summary>
    public static bool TryNewName(string input, out string name, out string error)
    {
        if (!TryName(input, out name, out error)) return false;
        if (IsGuestName(name))
        {
            name = "";
            return Fail("Names like 'Guest' or 'Guest1234' are kept for guests; pick another.", out error);
        }
        return true;
    }

    public static bool TryPassword(string input, out string error)
    {
        input ??= "";
        if (input.Length < PasswordMin || input.Length > PasswordMax)
            return Fail($"Passwords are {PasswordMin}-{PasswordMax} characters.", out error);
        foreach (var c in input)
        {
            if (c < 0x20 || c > 0x7E)
                return Fail("Passwords can only use printable ASCII characters (no accents or emoji).", out error);
        }
        error = "";
        return true;
    }

    /// <param name="normalized">Trimmed, lower-cased email on success, empty on failure.</param>
    public static bool TryEmail(string input, out string normalized, out string error)
    {
        normalized = "";
        var e = (input ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0 || e.Length > EmailMax)
            return Fail("Enter a valid email address.", out error);
        var at = e.IndexOf('@');
        if (at <= 0 || at != e.LastIndexOf('@') || e.Any(char.IsWhiteSpace))
            return Fail("Enter a valid email address.", out error);
        var domain = e[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        if (dot <= 0 || dot == domain.Length - 1)
            return Fail("Enter a valid email address.", out error);
        normalized = e;
        error = "";
        return true;
    }

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
