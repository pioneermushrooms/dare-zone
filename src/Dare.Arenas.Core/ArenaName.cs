namespace Dare.Arenas.Core;

/// <summary>Rules for names of player-owned arenas.</summary>
/// <remarks>
/// 3-15 characters, a-z and 0-9 only: SubspaceServer v4.0.0 (ArenaManager.CompleteGo) turns every
/// other character into 'x' and cuts names at 15 (Constants.MaxArenaNameLength) when a player
/// travels, so a name with '-' or a 16th character could be approved but never entered.
/// </remarks>
public static class ArenaName
{
    private const int MinLength = 3;
    private const int MaxLength = 15;

    private static readonly string Rule = $"Arena names are {MinLength}-{MaxLength} letters and digits (a-z, 0-9), start with a letter and do not end in a digit.";

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "public", "2v2pub", "3v3pub", "4v4pub", "duel",
        // staff.conf sections the server reserves (CapabilityManager.IsReservedStaffConfSection):
        // arena-scoped groups can't be stored for arenas with these names.
        "general", "grouppasswords",
    };

    /// <param name="existing">Folder names already in <c>arenas/</c>; compared case-insensitively.</param>
    /// <param name="normalized">Lower-case name on success, empty string on failure.</param>
    /// <param name="error">Empty on success, otherwise a player-facing reason.</param>
    public static bool TryValidate(string input, IReadOnlySet<string> existing, out string normalized, out string error)
    {
        normalized = "";
        input ??= "";
        var name = input.ToLowerInvariant();

        if (name.Length < MinLength || name.Length > MaxLength)
            return Fail(Rule, out error);

        if (name[0] is < 'a' or > 'z')
            return Fail("Arena names must start with a letter.", out error);

        if (name.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9'))))
            return Fail(Rule, out error);

        if (char.IsAsciiDigit(name[^1]))
            return Fail("Arena names must not end in a digit.", out error);

        if (Reserved.Contains(name))
            return Fail("That name is reserved.", out error);

        if (existing.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
            return Fail("An arena with that name already exists.", out error);

        normalized = name;
        error = "";
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
