using System.Text.Json;

namespace Dare.Arenas.Core;

/// <summary>One named settings set in a pilot's collection (no values).</summary>
public sealed record PilotSettingSet(long Id, string Name, int Count, DateTimeOffset Updated);

// A pilot's named settings sets: Section:Key -> value pairs they saved from an arena's settings
// page or from a settings file. Nothing here touches an arena; a set reaches one only through
// the settings page's Apply, which runs every value through the same checks as a single edit.
public sealed partial class ArenaStore
{
    public const int MaxPilotSettingSets = 25;
    public const int MaxSettingsPerSet = 2000;

    private void EnsureSettingSetTable() => Exec("""
        CREATE TABLE IF NOT EXISTS pilot_setting_sets (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          owner TEXT NOT NULL COLLATE NOCASE,
          name TEXT NOT NULL,
          settings TEXT NOT NULL,
          count INTEGER NOT NULL,
          updated TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_pilot_setting_sets_owner ON pilot_setting_sets(owner);
        """);

    /// <summary>A set's display name: letters, digits, spaces and - _ . # ( ) only, at most 40.</summary>
    public static string SettingSetName(string? name)
    {
        var n = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '#' or '-' or '_' or '.' or '(' or ')').ToArray()).Trim();
        if (n.Length > MaxMapNameLength) n = n[..MaxMapNameLength].Trim();
        return n.Length == 0 ? "settings" : n;
    }

    /// <summary>
    /// Checks a set before it is stored: 1 to <see cref="MaxSettingsPerSet"/> settings, each a
    /// <c>Section:Key</c> name with a value that passes <see cref="SettingValue"/>. Whether a key is
    /// one owners may change is checked when the set is applied (the allow-list can change).
    /// </summary>
    public static bool TryCheckSettingSet(IReadOnlyDictionary<string, string> settings, out SortedDictionary<string, string> clean, out string error)
    {
        clean = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = "";
        if (settings.Count == 0) { error = "A set needs at least one setting."; return false; }
        if (settings.Count > MaxSettingsPerSet) { error = $"A set holds at most {MaxSettingsPerSet} settings."; return false; }
        foreach (var (name, raw) in settings)
        {
            if (!SettingRef.TryParse(name, out var section, out var key)) { error = $"'{name}' isn't a Section:Key name."; return false; }
            if (!SettingValue.TryValidate(section, key, raw, out var value, out var bad)) { error = $"{section}:{key}: {bad}"; return false; }
            clean[$"{section}:{key}"] = value;
        }
        return true;
    }

    public IReadOnlyList<PilotSettingSet> SettingSets(string owner)
    {
        var list = new List<PilotSettingSet>();
        lock (_gate)
        {
            using var c = Cmd("SELECT id, name, count, updated FROM pilot_setting_sets WHERE (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o)) ORDER BY updated DESC, id DESC", ("$o", owner), ("$p", PersonParam(owner)));
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), Parse(r.GetString(3))));
        }
        return list;
    }

    /// <summary>The set's name and settings, or null when it isn't <paramref name="owner"/>'s.</summary>
    public (string Name, IReadOnlyDictionary<string, string> Settings)? SettingSetOf(string owner, long id)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT name, settings FROM pilot_setting_sets WHERE id = $id AND (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$id", id), ("$o", owner), ("$p", PersonParam(owner)));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1)) ?? new();
            return (r.GetString(0), map);
        }
    }

    /// <summary>A new set when <paramref name="id"/> is null (at most <see cref="MaxPilotSettingSets"/>), else
    /// replaces that one of <paramref name="owner"/>'s. Checked with <see cref="TryCheckSettingSet"/>.</summary>
    public Result SaveSettingSet(string owner, long? id, string name, IReadOnlyDictionary<string, string> settings, out long savedId)
    {
        savedId = 0;
        if (!TryCheckSettingSet(settings, out var clean, out var error)) return Fail(error);
        var display = SettingSetName(name);
        var json = JsonSerializer.Serialize(clean);
        long newId = 0;
        var result = Tx(() =>
        {
            if (id is { } existing)
            {
                if (Scalar("SELECT COUNT(*) FROM pilot_setting_sets WHERE id = $id AND (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$id", existing), ("$o", owner), ("$p", PersonParam(owner))) == 0)
                    return Fail("That settings set isn't in your collection.");
                Run("UPDATE pilot_setting_sets SET name = $n, settings = $s, count = $c, updated = $t WHERE id = $id",
                    ("$n", display), ("$s", json), ("$c", clean.Count), ("$t", Now()), ("$id", existing));
                newId = existing;
                return Ok($"Saved {display} ({Plural(clean.Count)}).");
            }
            if (Scalar("SELECT COUNT(*) FROM pilot_setting_sets WHERE (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$o", owner), ("$p", PersonParam(owner))) >= MaxPilotSettingSets)
                return Fail($"You have {MaxPilotSettingSets} settings sets; delete one to add another.");
            Run("INSERT INTO pilot_setting_sets(person_id, owner, name, settings, count, updated) VALUES($p,$o,$n,$s,$c,$t)",
                ("$o", owner), ("$p", PersonParam(owner)), ("$n", display), ("$s", json), ("$c", clean.Count), ("$t", Now()));
            newId = Scalar("SELECT last_insert_rowid()");
            return Ok($"Saved {display} ({Plural(clean.Count)}) to your settings sets.");
        });
        savedId = newId;
        return result;
    }

    public Result RemoveSettingSet(string owner, long id) => Tx(() =>
    {
        var name = SettingSetOf(owner, id)?.Name;
        if (name is null) return Fail("That settings set isn't in your collection.");
        Run("DELETE FROM pilot_setting_sets WHERE id = $id", ("$id", id));
        return Ok($"Deleted {name} from your settings sets.");
    });

    public Result RenameSettingSet(string owner, long id, string name) => Tx(() =>
    {
        if (SettingSetOf(owner, id) is null) return Fail("That settings set isn't in your collection.");
        var display = SettingSetName(name);
        Run("UPDATE pilot_setting_sets SET name = $n WHERE id = $id", ("$n", display), ("$id", id));
        return Ok($"Renamed it {display}.");
    });

    private static string Plural(int n) => n == 1 ? "1 setting" : $"{n} settings";
}
