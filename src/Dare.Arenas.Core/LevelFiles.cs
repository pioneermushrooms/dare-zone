namespace Dare.Arenas.Core;

/// <summary>Edits the comma-separated <c>General:LevelFiles</c> list.</summary>
public static class LevelFiles
{
    /// <summary>The non-empty, trimmed entries of <paramref name="current"/>.</summary>
    public static IReadOnlyList<string> Parse(string? current) =>
        (current ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Appends <paramref name="name"/> (trimmed). Refuses empty names, names containing ',',
    /// case-insensitive duplicates and lists already holding <paramref name="max"/> entries.
    /// On failure <paramref name="updated"/> is <paramref name="current"/> unchanged ("" if null).
    /// </summary>
    public static bool Add(string? current, string name, int max, out string updated, out string error)
    {
        updated = current ?? "";
        error = "";
        var n = name?.Trim() ?? "";
        if (n.Length == 0) { error = "A file name is required."; return false; }
        if (n.Contains(',')) { error = "File names must not contain ','."; return false; }

        var list = Parse(current).ToList();
        if (list.Any(e => string.Equals(e, n, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"{n} is already in the arena's LevelFiles.";
            return false;
        }
        if (list.Count >= max)
        {
            error = $"An arena can have at most {max} .lvz files.";
            return false;
        }

        list.Add(n);
        updated = string.Join(",", list);
        return true;
    }

    public const int MaxFileNameLength = 32;

    /// <summary>
    /// A plain file name safe to store and list in LevelFiles: 1-32 chars of ASCII letters, digits,
    /// '.', '_' and '-', not starting with '.'.
    /// </summary>
    public static bool IsSafeFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxFileNameLength || name[0] == '.') return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        return true;
    }
}

