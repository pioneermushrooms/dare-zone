using System.Globalization;

namespace Dare.Arenas.Core;

/// <summary>The server modules a game mode may attach to an arena (GameMode:Attach). Nothing else can be attached this way.</summary>
[Flags]
public enum GameModeModules
{
    None = 0,
    Kills = 1,
    Flags = 2,
    Balls = 4,
    King = 8,
    Ships = 16,
    ShipChange = 32,
}

/// <summary>
/// Reads GameMode:Attach and works out which allow-listed modules to attach or detach.
/// Spec: docs/superpowers/specs/2026-11-28-game-mode-phase2-wizard-design.md.
/// </summary>
public static class GameModeAttach
{
    private static readonly (GameModeModules Flag, string Type)[] AllowList =
    [
        (GameModeModules.Kills, "SS.Core.Modules.Scoring.KillPoints"),
        (GameModeModules.Flags, "SS.Core.Modules.Scoring.FlagGamePoints"),
        (GameModeModules.Balls, "SS.Core.Modules.Scoring.BallGamePoints"),
        (GameModeModules.King, "SS.Core.Modules.Scoring.Koth"),
        (GameModeModules.Ships, "SS.Core.Modules.Enforcers.LegalShip"),
        (GameModeModules.ShipChange, "SS.Core.Modules.Enforcers.ShipChange"),
    ];

    private const GameModeModules All = GameModeModules.Kills | GameModeModules.Flags | GameModeModules.Balls
        | GameModeModules.King | GameModeModules.Ships | GameModeModules.ShipChange;

    private static readonly char[] Separators = [',', ' ', '\t', ';', '\r', '\n'];

    /// <summary>Names (any case, separated by commas, spaces or semicolons) or a bit number. Unknown words are collected; bits off the list mean nothing.</summary>
    public static GameModeModules Parse(string? value, out IReadOnlyList<string> unknown)
    {
        var bad = new List<string>();
        unknown = bad;
        if (string.IsNullOrWhiteSpace(value)) return GameModeModules.None;
        var text = value.Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
            return bits >= 0 && (bits & ~(long)All) == 0 ? (GameModeModules)bits : GameModeModules.None;
        var result = GameModeModules.None;
        foreach (var word in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<GameModeModules>(word, ignoreCase: true, out var m) && Enum.IsDefined(m) && !int.TryParse(word, out _))
                result |= m;
            else
                bad.Add(word);
        }
        return result;
    }

    /// <summary>The module type names for <paramref name="m"/>, in allow-list order.</summary>
    public static IReadOnlyList<string> TypeNames(GameModeModules m) =>
        AllowList.Where(a => m.HasFlag(a.Flag)).Select(a => a.Type).ToList();

    /// <summary>
    /// What to attach (wanted, not yet attached by us or by staff) and detach (attached by us, no longer wanted,
    /// never one staff attached through Modules:AttachModules).
    /// </summary>
    public static (IReadOnlyList<string> Attach, IReadOnlyList<string> Detach) Diff(
        IReadOnlySet<string> attachedByUs, GameModeModules wanted, IReadOnlySet<string> staffAttached)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        bool In(IReadOnlySet<string> set, string t) => set.Any(s => cmp.Equals(s, t));
        var want = TypeNames(wanted);
        var attach = want.Where(t => !In(attachedByUs, t) && !In(staffAttached, t)).ToList();
        var detach = attachedByUs.Where(t => !want.Contains(t, cmp) && !In(staffAttached, t)).ToList();
        return (attach, detach);
    }

    /// <summary>
    /// Corrects what we recorded once the module manager answers (we record a change before awaiting it):
    /// a failed attach isn't ours (it may be someone else's, attached by command), a failed detach still is.
    /// </summary>
    public static void Settle(ISet<string> ours, string type, bool attaching, bool ok)
    {
        if (ok) return;
        if (attaching) ours.Remove(type); else ours.Add(type);
    }

    /// <summary>The type names in a Modules:AttachModules value (split like the server: whitespace, ':' and ';').</summary>
    public static IReadOnlySet<string> StaffAttached(string? attachModules)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(attachModules)) return set;
        foreach (var t in attachModules.Split([' ', '\t', '\r', '\n', ':', ';', '\\'], StringSplitOptions.RemoveEmptyEntries))
            set.Add(t);
        return set;
    }
}
