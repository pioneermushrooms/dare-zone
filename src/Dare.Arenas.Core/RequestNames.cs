namespace Dare.Arenas.Core;

/// <summary>
/// Finds the pending request staff mean when they type a name. Arena names have no spaces,
/// but people type them with spaces ("yo mama"), quotes, other capitals, or as the server
/// spells a visited name: SubspaceServer turns every character it doesn't allow into 'x' on
/// ?go, so "?go yo mama" visits "yoxmama".
/// </summary>
public static class RequestNames
{
    /// <summary>The pending name <paramref name="typed"/> means, or null.</summary>
    public static string? Find(string typed, IEnumerable<string> pending)
    {
        var squashed = Squash(typed);
        if (squashed.Length == 0) return null;
        var names = pending.ToList();
        var exact = names.FirstOrDefault(p => string.Equals(p, squashed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        // one 'x' that stood for a space
        for (var i = squashed.IndexOf('x'); i >= 0; i = squashed.IndexOf('x', i + 1))
        {
            var without = squashed.Remove(i, 1);
            var match = names.FirstOrDefault(p => string.Equals(p, without, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>
    /// "?declinearena &lt;name&gt; &lt;reason&gt;" where the name may contain spaces: the longest run of
    /// leading words that names a pending request is the name, the rest is the reason. With no
    /// match, the first word is the name.
    /// </summary>
    public static (string Name, string Reason) SplitNameAndReason(string parameters, IEnumerable<string> pending)
    {
        var words = (parameters ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return ("", "");
        var names = pending.ToList();
        for (var k = words.Length; k >= 1; k--)
        {
            var match = Find(string.Join(' ', words.Take(k)), names);
            if (match is not null) return (match, string.Join(' ', words.Skip(k)));
        }
        return (words[0].ToLowerInvariant(), string.Join(' ', words.Skip(1)));
    }

    /// <summary>The reply when nothing matched, naming what staff can decide.</summary>
    public static string NotFound(string typed, IReadOnlyCollection<string> pending) =>
        $"No pending request for '{typed.Trim()}'. "
        + (pending.Count == 0 ? "Nothing is pending." : $"Pending: {string.Join(", ", pending)}.");

    private static string Squash(string typed) =>
        new string((typed ?? "").Where(c => !char.IsWhiteSpace(c) && c is not ('\'' or '"')).ToArray()).ToLowerInvariant();
}
