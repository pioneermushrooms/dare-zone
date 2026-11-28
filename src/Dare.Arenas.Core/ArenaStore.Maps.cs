namespace Dare.Arenas.Core;

/// <summary>One map in a pilot's collection (no bytes).</summary>
public sealed record PilotMap(long Id, string Name, int Size, DateTimeOffset Updated);

// A pilot's collected maps: .lvl files they uploaded or saved from the map editor. Nothing here
// touches an arena; a map reaches one only when its owner publishes it from the editor.
public sealed partial class ArenaStore
{
    public const int MaxPilotMaps = 25;
    public const int MaxMapNameLength = 40;

    /// <summary>A display name from an uploaded file name: no directory, no ".lvl", safe characters, at most 40.</summary>
    public static string MapDisplayName(string? fileName)
    {
        var n = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        if (n.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        n = new string(n.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '#' or '-' or '_' or '.').ToArray()).Trim();
        if (n.Length > MaxMapNameLength) n = n[..MaxMapNameLength].Trim();
        return n.Length == 0 ? "map" : n;
    }

    public IReadOnlyList<PilotMap> Maps(string owner)
    {
        var list = new List<PilotMap>();
        lock (_gate)
        {
            using var c = Cmd("SELECT id, name, length(data), updated FROM pilot_maps WHERE (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o)) ORDER BY updated DESC, id DESC", ("$o", owner), ("$p", PersonParam(owner)));
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), Parse(r.GetString(3))));
        }
        return list;
    }

    /// <summary>The map's bytes and name, or null when it isn't <paramref name="owner"/>'s.</summary>
    public (string Name, byte[] Data)? MapFile(string owner, long id)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT name, data FROM pilot_maps WHERE id = $id AND (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$id", id), ("$o", owner), ("$p", PersonParam(owner)));
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetString(0), (byte[])r.GetValue(1)) : null;
        }
    }

    /// <summary>
    /// Saves a map: a new one when <paramref name="id"/> is null (at most <see cref="MaxPilotMaps"/>), else
    /// replaces that one of <paramref name="owner"/>'s. A new map whose bytes the pilot already has is not
    /// added twice: the existing one moves to the top and its id is returned. The caller has checked the
    /// bytes (UploadPolicy).
    /// </summary>
    public Result SaveMap(string owner, long? id, string name, byte[] data, out long savedId)
    {
        savedId = 0;
        var display = MapDisplayName(name);
        long newId = 0;
        var result = Tx(() =>
        {
            if (id is { } existing)
            {
                if (Scalar("SELECT COUNT(*) FROM pilot_maps WHERE id = $id AND (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$id", existing), ("$o", owner), ("$p", PersonParam(owner))) == 0)
                    return Fail("That map isn't in your collection.");
                Run("UPDATE pilot_maps SET name = $n, data = $d, updated = $t WHERE id = $id",
                    ("$n", display), ("$d", data), ("$t", Now()), ("$id", existing));
                newId = existing;
                return Ok($"Saved {display} to your maps.");
            }
            var same = Scalar("SELECT COALESCE(MAX(id), 0) FROM pilot_maps WHERE (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o)) AND data = $d", ("$o", owner), ("$p", PersonParam(owner)), ("$d", data));
            if (same != 0)
            {
                Run("UPDATE pilot_maps SET updated = $t WHERE id = $id", ("$t", Now()), ("$id", same));
                newId = same;
                return Ok($"{MapFile(owner, same)!.Value.Name} is already in your maps.");
            }
            if (Scalar("SELECT COUNT(*) FROM pilot_maps WHERE (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$o", owner), ("$p", PersonParam(owner))) >= MaxPilotMaps)
                return Fail($"You have {MaxPilotMaps} maps; remove one to add another.");
            Run("INSERT INTO pilot_maps(person_id, owner, name, data, updated) VALUES($p,$o,$n,$d,$t)",
                ("$o", owner), ("$p", PersonParam(owner)), ("$n", display), ("$d", data), ("$t", Now()));
            newId = Scalar("SELECT last_insert_rowid()");
            return Ok($"Added {display} to your maps.");
        });
        savedId = newId;
        return result;
    }

    public Result RemoveMap(string owner, long id) => Tx(() =>
    {
        var name = MapFile(owner, id)?.Name;
        if (name is null) return Fail("That map isn't in your collection.");
        Run("DELETE FROM pilot_maps WHERE id = $id", ("$id", id));
        return Ok($"Removed {name} from your maps.");
    });

    public Result RenameMap(string owner, long id, string name) => Tx(() =>
    {
        if (MapFile(owner, id) is null) return Fail("That map isn't in your collection.");
        var display = MapDisplayName(name);
        Run("UPDATE pilot_maps SET name = $n WHERE id = $id", ("$n", display), ("$id", id));
        return Ok($"Renamed it {display}.");
    });
}
