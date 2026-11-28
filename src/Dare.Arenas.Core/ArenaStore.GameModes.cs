using System.Text;
using System.Text.Json;

namespace Dare.Arenas.Core;

/// <summary>One game mode in an account's collection (no values).</summary>
public sealed record PilotGameMode(long Id, string Name, string MadeBy, int Count, DateTimeOffset Updated);

// An account's game modes (docs/superpowers/specs/2026-11-27-game-mode-phase1-ships-design.md).
// Keyed by person, so every pilot name on the account sees the same ones; made_by is credit only.
// `data` is the editor's own JSON (wizard answers, ship roles, later labels and rules); `settings`
// is the Section:Key -> value map the editor computed from it, checked like a settings set. A game
// mode reaches an arena only through the settings Apply, which checks every value again.
public sealed partial class ArenaStore
{
    public const int MaxPilotGameModes = 25;
    public const int MaxGameModeDataBytes = 64 * 1024;

    private void EnsureGameModeTable() => Exec("""
        CREATE TABLE IF NOT EXISTS pilot_game_modes (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          person_id INTEGER NOT NULL,
          made_by TEXT NOT NULL,
          name TEXT NOT NULL,
          data TEXT NOT NULL,
          settings TEXT NOT NULL,
          count INTEGER NOT NULL,
          updated TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_pilot_game_modes_person ON pilot_game_modes(person_id);
        """);

    /// <summary>A JSON object of at most <see cref="MaxGameModeDataBytes"/> with an integer <c>version</c> of 1 or more.</summary>
    public static bool TryCheckGameModeData(string data, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(data)) { error = "A game mode needs its data."; return false; }
        if (Encoding.UTF8.GetByteCount(data) > MaxGameModeDataBytes) { error = $"A game mode's data must be {MaxGameModeDataBytes / 1024} KB or smaller."; return false; }
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("version", out var v)
                || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 1)
            {
                error = "A game mode's data must be an object with a version.";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "A game mode's data isn't valid JSON.";
            return false;
        }
        return true;
    }

    public IReadOnlyList<PilotGameMode> GameModes(long person)
    {
        var list = new List<PilotGameMode>();
        lock (_gate)
        {
            using var c = Cmd("SELECT id, name, made_by, count, updated FROM pilot_game_modes WHERE person_id = $p ORDER BY updated DESC, id DESC", ("$p", person));
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Parse(r.GetString(4))));
        }
        return list;
    }

    /// <summary>The game mode, or null when it isn't <paramref name="person"/>'s.</summary>
    public (string Name, string MadeBy, string Data, IReadOnlyDictionary<string, string> Settings)? GameModeOf(long person, long id)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT name, made_by, data, settings FROM pilot_game_modes WHERE id = $id AND person_id = $p", ("$id", id), ("$p", person));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3)) ?? new();
            return (r.GetString(0), r.GetString(1), r.GetString(2), map);
        }
    }

    /// <summary>A new game mode when <paramref name="id"/> is null (at most <see cref="MaxPilotGameModes"/>), else
    /// replaces that one of <paramref name="person"/>'s (made_by stays the creator).</summary>
    public Result SaveGameMode(long person, string madeBy, long? id, string name, string data, IReadOnlyDictionary<string, string> settings, out long savedId)
    {
        savedId = 0;
        if (!TryCheckGameModeData(data, out var error)) return Fail(error);
        if (!TryCheckSettingSet(settings, out var clean, out error)) return Fail(error);
        var display = SettingSetName(name);
        var json = JsonSerializer.Serialize(clean);
        long newId = 0;
        var result = Tx(() =>
        {
            if (id is { } existing)
            {
                if (Scalar("SELECT COUNT(*) FROM pilot_game_modes WHERE id = $id AND person_id = $p", ("$id", existing), ("$p", person)) == 0)
                    return Fail("That game mode isn't in your collection.");
                Run("UPDATE pilot_game_modes SET name = $n, data = $d, settings = $s, count = $c, updated = $t WHERE id = $id",
                    ("$n", display), ("$d", data), ("$s", json), ("$c", clean.Count), ("$t", Now()), ("$id", existing));
                newId = existing;
                return Ok($"Saved {display}.");
            }
            if (Scalar("SELECT COUNT(*) FROM pilot_game_modes WHERE person_id = $p", ("$p", person)) >= MaxPilotGameModes)
                return Fail($"You have {MaxPilotGameModes} game modes; delete one to add another.");
            Run("INSERT INTO pilot_game_modes(person_id, made_by, name, data, settings, count, updated) VALUES($p,$m,$n,$d,$s,$c,$t)",
                ("$p", person), ("$m", (madeBy ?? "").Trim()), ("$n", display), ("$d", data), ("$s", json), ("$c", clean.Count), ("$t", Now()));
            newId = Scalar("SELECT last_insert_rowid()");
            return Ok($"Saved {display} to your game modes.");
        });
        savedId = newId;
        return result;
    }

    public Result RenameGameMode(long person, long id, string name) => Tx(() =>
    {
        if (GameModeOf(person, id) is null) return Fail("That game mode isn't in your collection.");
        var display = SettingSetName(name);
        Run("UPDATE pilot_game_modes SET name = $n WHERE id = $id", ("$n", display), ("$id", id));
        return Ok($"Renamed it {display}.");
    });

    /// <summary>A copy named "&lt;name&gt; (copy)"; counts against the cap.</summary>
    public Result DuplicateGameMode(long person, long id, out long newId)
    {
        newId = 0;
        if (GameModeOf(person, id) is not { } mode) return Fail("That game mode isn't in your collection.");
        var baseName = mode.Name.Length > MaxMapNameLength - 7 ? mode.Name[..(MaxMapNameLength - 7)].Trim() : mode.Name;
        return SaveGameMode(person, mode.MadeBy, null, baseName + " (copy)", mode.Data, mode.Settings, out newId);
    }

    public Result RemoveGameMode(long person, long id) => Tx(() =>
    {
        var name = GameModeOf(person, id)?.Name;
        if (name is null) return Fail("That game mode isn't in your collection.");
        Run("DELETE FROM pilot_game_modes WHERE id = $id", ("$id", id));
        return Ok($"Deleted {name} from your game modes.");
    });
}
