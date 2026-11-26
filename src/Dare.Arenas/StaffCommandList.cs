using System.Collections;
using System.Reflection;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

// The Admin page's command reference: every command the running server has registered, with its
// help text, the module that added it and whether the signed-in staff member may use it.
// ICommandManager can't list its commands, so this reads SS.Core's CommandManager by reflection
// (its private _commands table, the one ?allcommands walks). If a server update renames those
// fields the page says the list is unavailable instead of failing.
public sealed partial class ArenaOwnership
{
    /// <summary>One registered command for the Admin page.</summary>
    internal sealed record CommandInfo(string Name, string Module, string? Arena, string? Help);

    /// <summary>The Admin page's command list for <paramref name="player"/>; null if the server's table can't be read. Mainloop.</summary>
    internal object? StaffCommands(string player)
    {
        var commands = ReadCommandTable();
        if (commands is null) return null;
        return commands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new
            {
                name = c.Name,
                module = c.Module,
                arena = c.Arena,
                help = c.Help,
                // cmd_x: typed in public/arena chat; privcmd_x: sent to a player (/?x)
                can = _capabilityManager.HasCapability(player, "cmd_" + c.Name),
                canPriv = _capabilityManager.HasCapability(player, "privcmd_" + c.Name),
            })
            .ToList();
    }

    private List<CommandInfo>? ReadCommandTable()
    {
        try
        {
            var cm = _commandManager;
            var type = cm.GetType();
            var table = type.GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(cm);
            var rwLock = type.GetField("_rwLock", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(cm) as ReaderWriterLockSlim;
            if (table is not IDictionary dict) return null;

            var result = new List<CommandInfo>();
            rwLock?.EnterReadLock();
            try
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is not string name || entry.Value is not IEnumerable list) continue;
                    foreach (var data in list)
                    {
                        if (data is null) continue;
                        var dt = data.GetType();
                        var arena = dt.GetField("Arena")?.GetValue(data) as Arena;
                        var help = dt.GetField("HelpText")?.GetValue(data) as string;
                        var handler = dt.GetField("Handler")?.GetValue(data) as Delegate;
                        result.Add(new CommandInfo(name, CommandModule(handler), arena?.Name, Tidy(help)));
                    }
                }
            }
            finally
            {
                if (rwLock?.IsReadLockHeld == true) rwLock.ExitReadLock();
            }
            return result;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"Could not read the server's command table for the Admin page: {ex.Message}");
            return null;
        }
    }

    /// <summary>"SS.Core.Modules.ArenaManager" -> "ArenaManager"; nested/compiler types use their outer class.</summary>
    private static string CommandModule(Delegate? handler)
    {
        var t = handler?.Method.DeclaringType;
        while (t?.DeclaringType is not null && t.Name.Contains('<')) t = t.DeclaringType;
        while (t?.IsNested == true && t.Name.StartsWith('<')) t = t.DeclaringType;
        return t?.Name ?? "?";
    }

    private static string? Tidy(string? help) =>
        string.IsNullOrWhiteSpace(help) ? null : help.Replace("\r", "").Trim();
}
