using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// Arena admins (staff whose [(global)] group has cmd_arenaadmin: sysop and smod) run every arena
// from the web editor, not only the ones the store says they own or co-mod. They get the owner
// role in every active player arena and in every zone arena with its own arenas/<name>/arena.conf.
// Safe off the mainloop: the store locks, and the capability check reads config under its lock.
public sealed partial class ArenaOwnership
{
    /// <summary>The editor's arenas for <paramref name="player"/>: their own roles first, then (for arena admins) every other arena by name.</summary>
    internal IReadOnlyList<(string Arena, string Role)> WebRolesFor(string player)
    {
        var roles = Store.RolesFor(player);
        if (!WebStaffCan(player, AdminCapability)) return roles;
        var listed = roles.Select(r => r.Arena).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = StaffArenaNames().Where(listed.Add).Select(n => (n, "owner"));
        return roles.Concat(extra).ToList();
    }

    /// <summary>The role <paramref name="player"/> has in <paramref name="arena"/> on the web: their own, else owner for an arena admin.</summary>
    internal string? WebRoleIn(string arena, string player) =>
        Store.RoleIn(arena, player)
        ?? (!arena.StartsWith('#') && WebStaffCan(player, AdminCapability) && StaffArenaNames().Contains(arena, StringComparer.OrdinalIgnoreCase)
            ? "owner" : null);

    /// <summary>Arenas an admin may edit: active player arenas and zone arenas with their own arena.conf; not suspended, archived or template folders.</summary>
    private IEnumerable<string> StaffArenaNames()
    {
        var inactive = Store.Arenas().Where(a => a.Status != ArenaStatus.Active).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> folders;
        try
        {
            folders = Directory.Exists(ArenasDir)
                ? Directory.EnumerateDirectories(ArenasDir).Select(Path.GetFileName).OfType<string>().ToList()
                : [];
        }
        catch (IOException) { folders = []; }
        catch (UnauthorizedAccessException) { folders = []; }
        return folders
            .Where(n => n.Length > 0 && (n == PublicCounts.PublicGroup || n[0] is not ('_' or '(' or '#' or '.')) && !inactive.Contains(n)
                        && ArenaConfigFile.OwnPath(ArenasDir, n) is not null)
            .OrderBy(n => n != PublicCounts.PublicGroup).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gives the public arenas their own arenas/(public)/arena.conf, a copy of arenas/(default)/arena.conf,
    /// so staff can edit them without changing every arena that falls back to the default. Same content,
    /// so nothing changes in play; relative #includes (flags.conf, balls.conf) still find the default's files.
    /// </summary>
    private void EnsurePublicConf()
    {
        try
        {
            var own = Path.Combine(ArenasDir, PublicCounts.PublicGroup, ArenaConfigFile.FileName);
            var fallback = Path.Combine(ArenasDir, "(default)", ArenaConfigFile.FileName);
            if (File.Exists(own) || !File.Exists(fallback)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.Copy(fallback, own);
            Log(LogLevel.Info, $"Created {own} from {fallback}, so the public arena has settings of its own to edit.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Warn, $"Could not give the public arena its own arena.conf: {ex.Message}");
        }
    }
}
