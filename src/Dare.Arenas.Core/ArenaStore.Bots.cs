namespace Dare.Arenas.Core;

public sealed record ArenaBot(string Label, string Script, bool Deployed)
{
    /// <summary>The team (frequency) from the script's <c>team</c> line; 0 when it has none.</summary>
    public int Team => BotCommand.Team(Script);

    /// <summary>The 1-9 priority from the script's <c>priority</c> line; 5 when it has none.</summary>
    public int Priority => BotCommand.Priority(Script);
}

public sealed partial class ArenaStore
{
    public const int MaxBotsPerArena = 16;
    public const int MaxBotLabel = 20;
    public const int MaxMatchSize = 16;

    public IReadOnlyList<ArenaBot> Bots(string arena)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT label, script, label NOT IN (SELECT label FROM stopped_bots) FROM bots WHERE arena=$a ORDER BY label", ("$a", arena));
            using var reader = cmd.ExecuteReader();
            var bots = new List<ArenaBot>();
            while (reader.Read()) bots.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            return bots;
        }
    }

    public Result SaveBot(string arena, string owner, string label, string? script)
    {
        label = label.Trim();
        if (label.Length is < 1 or > MaxBotLabel || !char.IsAsciiLetterOrDigit(label[0]) ||
            label.Contains("  ", StringComparison.Ordinal) || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '_' or '-' or '.')))
            return new(false, "Use 1–20 letters, digits, single spaces, underscores, hyphens or periods. Start with a letter or digit.");
        if (script is not null && !ValidBotScript(script))
            return new(false, "Invalid bot settings. Check the ship, behavior, team, priority, coordinates and chat lines.");

        return Tx(() =>
        {
            if (Load(arena) is not { Status: ArenaStatus.Active } owned ||
                !string.Equals(owned.Owner, owner, StringComparison.OrdinalIgnoreCase))
                return new(false, "Only the active arena's owner can manage bots.");

            if (script is null)
            {
                Run("DELETE FROM stopped_bots WHERE label IN (SELECT label FROM bots WHERE arena=$a AND label=$l)", ("$a", arena), ("$l", label));
                Run("DELETE FROM bots WHERE arena=$a AND label=$l", ("$a", arena), ("$l", label));
            }
            else
            {
                using var name = Cmd("SELECT arena FROM bots WHERE label=$l", ("$l", label));
                var existing = name.ExecuteScalar() as string;
                if (existing is not null && existing != arena) return new(false, "That bot name is already in use.");
                if (existing is null && Bots(arena).Count >= MaxBotsPerArena) return new(false, $"This arena already has {MaxBotsPerArena} bots.");
                if (existing is null) Run("INSERT INTO stopped_bots(label) VALUES($l)", ("$l", label));
                Run("INSERT INTO bots(arena,label,script) VALUES($a,$l,$s) ON CONFLICT(label) DO UPDATE SET label=excluded.label,script=excluded.script",
                    ("$a", arena), ("$l", label), ("$s", script));
            }

            AddEvent(Now(), arena, owner, script is null ? "bot-remove" : "bot-save", label);
            return new(true, script is null ? "Bot removed." : "Bot saved.");
        });
    }

    /// <summary>True when <paramref name="name"/> is a bot in the register (any arena).</summary>
    public bool IsBotName(string name)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT 1 FROM bots WHERE label=$l", ("$l", name));
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Every registered bot name, compared without regard to case.</summary>
    public IReadOnlySet<string> BotNames()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT label FROM bots");
            using var reader = cmd.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        }
    }

    /// <summary>The arena's deployed bots whose name no pilot account holds.</summary>
    public IReadOnlyList<ArenaBot> LaunchableBots(string arena, Func<string, bool> isAccount) =>
        Bots(arena).Where(b => b.Deployed && !isAccount(b.Label)).ToList();

    /// <summary>Why a saved bot will not launch, or null when nothing stops it.</summary>
    public string? BotProblem(string arena, string label, Func<string, bool> isAccount) =>
        Bots(arena).Any(b => string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase)) && isAccount(label)
            ? "Not launched: a pilot uses this name."
            : null;

    /// <summary>Bots per team (frequency) the match director fields in the arena.</summary>
    public IReadOnlyDictionary<int, int> MatchSizes(string arena)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT freq, size FROM bot_matches WHERE arena=$a ORDER BY freq", ("$a", arena));
            using var reader = cmd.ExecuteReader();
            var sizes = new Dictionary<int, int>();
            while (reader.Read()) sizes[reader.GetInt32(0)] = reader.GetInt32(1);
            return sizes;
        }
    }

    /// <summary>Sets how many bots a team fields (0-16); null removes the entry.</summary>
    public Result SetMatchSize(string arena, string owner, int freq, int? size)
    {
        if (freq is < 0 or > 9999) return new(false, "Team must be 0-9999.");
        if (size is < 0 or > MaxMatchSize) return new(false, $"Match size must be 0-{MaxMatchSize}.");
        return Tx(() =>
        {
            if (Load(arena) is not { Status: ArenaStatus.Active } owned ||
                !string.Equals(owned.Owner, owner, StringComparison.OrdinalIgnoreCase))
                return new(false, "Only the active arena's owner can manage bots.");
            if (size is { } n)
                Run("INSERT INTO bot_matches(arena,freq,size) VALUES($a,$f,$s) ON CONFLICT(arena,freq) DO UPDATE SET size=excluded.size",
                    ("$a", arena), ("$f", freq), ("$s", n));
            else
                Run("DELETE FROM bot_matches WHERE arena=$a AND freq=$f", ("$a", arena), ("$f", freq));
            AddEvent(Now(), arena, owner, size is null ? "match-remove" : "match-set", $"{freq} {size}");
            return new(true, size is null ? "Match size removed." : "Match size saved.");
        });
    }

    public Result SetBotDeployment(string arena, string owner, string label, bool deploy) => Tx(() =>
    {
        if (Load(arena) is not { Status: ArenaStatus.Active } owned ||
            !string.Equals(owned.Owner, owner, StringComparison.OrdinalIgnoreCase))
            return new(false, "Only the active arena's owner can manage bots.");
        var bot = Bots(arena).FirstOrDefault(b => string.Equals(b.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
        if (bot is null) return new(false, "No bot with that label in this arena.");

        if (bot.Deployed != deploy)
        {
            Run(deploy ? "DELETE FROM stopped_bots WHERE label=$l" : "INSERT INTO stopped_bots(label) VALUES($l)",
                ("$l", bot.Label));
            AddEvent(Now(), arena, owner, deploy ? "bot-deploy" : "bot-stop", bot.Label);
        }
        return new(true, deploy ? "Bot deployed. It joins when a pilot is in this arena." : "Bot stopped.");
    });

    private static bool ValidBotScript(string script)
    {
        if (script.Length is 0 or > 3900) return false;
        int points = 0, announcements = 0, replies = 0, casual = 0, teams = 0, priorities = 0;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length > 240 || line.Any(c => c is < ' ' or > '~')) return false;
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[1].Length == 0) return false;
            var value = parts[1];
            var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var valid = parts[0] switch
            {
                "ship" => int.TryParse(value, out int ship) && ship is >= 1 and <= 8,
                "behaviour" => value is "idle" or "patrol" or "sentry" or "fighter",
                "point" => ++points <= 64 && fields.Length == 2 &&
                    fields.All(p => int.TryParse(p, out int n) && n is >= 0 and <= 1023),
                "team" => ++teams == 1 && int.TryParse(value, out int team) && team is >= 0 and <= 9999,
                "priority" => ++priorities == 1 && int.TryParse(value, out int priority) && priority is >= 1 and <= 9,
                "range" => int.TryParse(value, out int range) && range is >= 1 and <= 200,
                "botchat" => value == "on",
                "casual" => ++casual == 1 && fields.Length == 2 &&
                    int.TryParse(fields[0], out int level) && level is >= 1 and <= 5 &&
                    fields[1] is "friendly" or "dry" or "competitive",
                "greet" => value.Length <= 200,
                "announce" => ++announcements <= 10 && fields.Length >= 2 && uint.TryParse(fields[0], out uint seconds) && seconds >= 60,
                "reply" => ++replies <= 20 && fields.Length >= 2,
                _ => false
            };
            if (!valid) return false;
        }
        return true;
    }
}
