namespace Dare.Arenas.Core;

/// <summary>Maps a player's role in an owned arena to the arena-scoped group the module grants.</summary>
public static class GrantLogic
{
    public const string OwnerGroup = "arenaowner";
    public const string CoModGroup = "arenamod";

    // Groups that are already at least as strong as arenamod; arenaowner adds owner commands on top of mod.
    private static readonly HashSet<string> NeverDowngrade = new(StringComparer.OrdinalIgnoreCase)
    {
        "smod", "sysop", "bot",
    };

    /// <summary><c>owner</c> → <c>arenaowner</c>, <c>comod</c> → <c>arenamod</c>, anything else → null.</summary>
    public static string? GroupFor(string? role) => role switch
    {
        "owner" => OwnerGroup,
        "comod" => CoModGroup,
        _ => null,
    };

    /// <summary>
    /// The group to set (arena-scoped) for a player with <paramref name="role"/> whose group in that
    /// arena is currently <paramref name="currentGroup"/>, or null when nothing should change:
    /// no role, already in that group, or already in a higher staff group (smod/sysop/bot; and
    /// <c>mod</c> for co-mods, since <c>arenamod</c> equals <c>mod</c>).
    /// </summary>
    public static string? GroupToApply(string? role, string? currentGroup)
    {
        var target = GroupFor(role);
        if (target is null) return null;
        var current = currentGroup ?? "";
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return null;
        if (NeverDowngrade.Contains(current)) return null;
        if (target == CoModGroup && string.Equals(current, "mod", StringComparison.OrdinalIgnoreCase)) return null;
        return target;
    }

    /// <summary>
    /// True when a player holds one of this module's groups (<c>arenaowner</c>/<c>arenamod</c>) but no
    /// longer has a role in the arena (co-mod removed, ownership transferred, arena suspended), so the
    /// stale arena-scoped grant should be removed. Other groups are never touched.
    /// </summary>
    public static bool ShouldRevoke(string? role, string? currentGroup)
    {
        if (role is not null || currentGroup is null) return false;
        return string.Equals(currentGroup, OwnerGroup, StringComparison.OrdinalIgnoreCase)
               || string.Equals(currentGroup, CoModGroup, StringComparison.OrdinalIgnoreCase);
    }
}

