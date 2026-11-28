namespace Dare.Arenas.Core;

/// <summary>One .lvz in a pilot's collection (no bytes).</summary>
public sealed record PilotLvz(long Id, string Name, int Size, DateTimeOffset Updated);

// A pilot's saved .lvz packages: the LVZ editor's library, drafts until published to an arena.
// The name doubles as the file name the arena stores it under, so it is kept LevelFiles-safe.
public sealed partial class ArenaStore
{
    public const int MaxPilotLvz = 10;

    /// <summary>A safe .lvz file name: letters, digits, '.', '_' and '-', ending ".lvz", at most 32 characters.</summary>
    public static string LvzFileName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/')).Trim();
        if (n.EndsWith(".lvz", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        n = new string(n.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray()).Trim('.', '_');
        if (n.Length > LevelFiles.MaxFileNameLength - 4) n = n[..(LevelFiles.MaxFileNameLength - 4)].TrimEnd('.');
        return (n.Length == 0 ? "graphics" : n) + ".lvz";
    }

    private void EnsureLvzTable() => Exec("""
        CREATE TABLE IF NOT EXISTS pilot_lvz (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          owner TEXT NOT NULL COLLATE NOCASE,
          name TEXT NOT NULL,
          data BLOB NOT NULL,
          updated TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_pilot_lvz_owner ON pilot_lvz(owner);
        """);

    public IReadOnlyList<PilotLvz> LvzList(string owner)
    {
        var list = new List<PilotLvz>();
        lock (_gate)
        {
            using var c = Cmd("SELECT id, name, length(data), updated FROM pilot_lvz WHERE (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o)) ORDER BY updated DESC, id DESC", ("$o", owner), ("$p", PersonParam(owner)));
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), Parse(r.GetString(3))));
        }
        return list;
    }

    public (string Name, byte[] Data)? LvzFileOf(string owner, long id)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT name, data FROM pilot_lvz WHERE id = $id AND (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$id", id), ("$o", owner), ("$p", PersonParam(owner)));
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetString(0), (byte[])r.GetValue(1)) : null;
        }
    }

    /// <summary>A new .lvz when <paramref name="id"/> is null (at most <see cref="MaxPilotLvz"/>), else replaces
    /// that one of <paramref name="owner"/>'s. The caller has checked the bytes (UploadPolicy).</summary>
    public Result SaveLvz(string owner, long? id, string name, byte[] data, out long savedId)
    {
        savedId = 0;
        var file = LvzFileName(name);
        long newId = 0;
        var result = Tx(() =>
        {
            if (id is { } existing)
            {
                if (Scalar("SELECT COUNT(*) FROM pilot_lvz WHERE id = $id AND (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$id", existing), ("$o", owner), ("$p", PersonParam(owner))) == 0)
                    return Fail("That .lvz isn't in your collection.");
                Run("UPDATE pilot_lvz SET name = $n, data = $d, updated = $t WHERE id = $id", ("$n", file), ("$d", data), ("$t", Now()), ("$id", existing));
                newId = existing;
                return Ok($"Saved {file} to your LVZ.");
            }
            if (Scalar("SELECT COUNT(*) FROM pilot_lvz WHERE (person_id = $p OR ($p IS NULL AND person_id IS NULL AND owner = $o))", ("$o", owner), ("$p", PersonParam(owner))) >= MaxPilotLvz)
                return Fail($"You have {MaxPilotLvz} .lvz files; remove one to add another.");
            Run("INSERT INTO pilot_lvz(person_id, owner, name, data, updated) VALUES($p,$o,$n,$d,$t)", ("$o", owner), ("$p", PersonParam(owner)), ("$n", file), ("$d", data), ("$t", Now()));
            newId = Scalar("SELECT last_insert_rowid()");
            return Ok($"Added {file} to your LVZ.");
        });
        savedId = newId;
        return result;
    }

    public Result RemoveLvz(string owner, long id) => Tx(() =>
    {
        var name = LvzFileOf(owner, id)?.Name;
        if (name is null) return Fail("That .lvz isn't in your collection.");
        Run("DELETE FROM pilot_lvz WHERE id = $id", ("$id", id));
        return Ok($"Removed {name} from your LVZ.");
    });

    public Result RenameLvz(string owner, long id, string name) => Tx(() =>
    {
        if (LvzFileOf(owner, id) is null) return Fail("That .lvz isn't in your collection.");
        var file = LvzFileName(name);
        Run("UPDATE pilot_lvz SET name = $n WHERE id = $id", ("$n", file), ("$id", id));
        return Ok($"Renamed it {file}.");
    });

    public Result DuplicateLvz(string owner, long id, out long copyId)
    {
        copyId = 0;
        var f = LvzFileOf(owner, id);
        if (f is null) return Fail("That .lvz isn't in your collection.");
        var stem = f.Value.Name[..^4];
        return SaveLvz(owner, null, (stem.Length > 23 ? stem[..23] : stem) + "-copy", f.Value.Data, out copyId);
    }
}
