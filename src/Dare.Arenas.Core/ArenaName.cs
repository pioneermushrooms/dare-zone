namespace Dare.Arenas.Core;

/// <summary>Rules for names of player-owned arenas.</summary>
public static class ArenaName
{
    private const int MinLength = 3;
    private const int MaxLength = 16;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "public", "2v2pub", "3v3pub", "4v4pub", "duel",
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
            return Fail($"Arena names must be {MinLength}-{MaxLength} characters long.", out error);

        if (name[0] is < 'a' or > 'z')
            return Fail("Arena names must start with a letter.", out error);

        if (name.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')))
            return Fail("Arena names may only contain letters, digits and hyphens.", out error);

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
