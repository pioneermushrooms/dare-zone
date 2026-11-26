using System.Text.Json;

namespace Dare.Arenas.Core;

/// <summary>One tileset in a pilot's collection (no bytes).</summary>
public sealed record PilotTileset(long Id, string Name, int Size, bool HasRoles, DateTimeOffset Updated);

/// <summary>
/// The checks a saved tileset must pass: a Continuum tileset BMP (304 x 160, 8-bit, plain or RLE8)
/// and, optionally, a role map in the tile layout format (docs/tile-layout.md).
/// </summary>
public static class TilesetFile
{
    public const int Width = 304, Height = 160;
    public const int MaxBmpBytes = 128 * 1024;
    public const int MaxRolesBytes = 64 * 1024;

    public static bool TryCheckBmp(byte[] b, out string error)
    {
        error = "";
        if (b.Length < 54 || b[0] != 'B' || b[1] != 'M') { error = "That isn't a BMP file."; return false; }
        if (b.Length > MaxBmpBytes) { error = "A tileset must be 128 KB or smaller."; return false; }
        int I32(int p) => BitConverter.ToInt32(b, p);
        var w = I32(18); var h = Math.Abs(I32(22)); var bpp = BitConverter.ToUInt16(b, 28); var comp = I32(30); var off = I32(10);
        if (w != Width || h != Height) { error = $"A tileset is {Width} x {Height} pixels; this one is {w} x {h}."; return false; }
        if (bpp != 8 || comp is not (0 or 1)) { error = "A tileset is an 8-bit (256-colour) BMP."; return false; }
        if (off < 54 || off >= b.Length) { error = "That BMP file is cut short."; return false; }
        if (comp == 0 && off + ((Width + 3) & ~3) * Height > b.Length) { error = "That BMP file is cut short."; return false; }
        return true;
    }

    /// <summary>Null and empty are fine (no role map); otherwise a JSON object of format "dare-tile-layout".</summary>
    public static bool TryCheckRoles(string? roles, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(roles)) return true;
        if (roles.Length > MaxRolesBytes) { error = "The role map must be 64 KB or smaller."; return false; }
        try
        {
            using var doc = JsonDocument.Parse(roles);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() == "dare-tile-layout")
                return true;
        }
        catch (JsonException) { }
        error = "The role map isn't a tile layout (docs/tile-layout.md).";
        return false;
    }
}

// A pilot's saved tilesets: the tile editor's library, next to their maps. Nothing here touches an
// arena; a tileset reaches one only inside a map that the pilot then publishes.
public sealed partial class ArenaStore
{
    public const int MaxPilotTilesets = 25;

    private void EnsureTilesetTable() => Exec("""
        CREATE TABLE IF NOT EXISTS pilot_tilesets (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          owner TEXT NOT NULL COLLATE NOCASE,
          name TEXT NOT NULL,
          bmp BLOB NOT NULL,
          roles TEXT NULL,
          updated TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_pilot_tilesets_owner ON pilot_tilesets(owner);
        """);

    public IReadOnlyList<PilotTileset> Tilesets(string owner)
    {
        var list = new List<PilotTileset>();
        lock (_gate)
        {
            using var c = Cmd("SELECT id, name, length(bmp), roles IS NOT NULL AND roles <> '', updated FROM pilot_tilesets WHERE owner = $o ORDER BY updated DESC, id DESC", ("$o", owner));
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3) != 0, Parse(r.GetString(4))));
        }
        return list;
    }

    /// <summary>The tileset's name, BMP and role map (or null), or null when it isn't <paramref name="owner"/>'s.</summary>
    public (string Name, byte[] Bmp, string? Roles)? TilesetFileOf(string owner, long id)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT name, bmp, roles FROM pilot_tilesets WHERE id = $id AND owner = $o", ("$id", id), ("$o", owner));
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetString(0), (byte[])r.GetValue(1), r.IsDBNull(2) ? null : r.GetString(2)) : null;
        }
    }

    /// <summary>A new tileset when <paramref name="id"/> is null (at most <see cref="MaxPilotTilesets"/>), else
    /// replaces that one of <paramref name="owner"/>'s. The caller has checked the bytes (TilesetFile).</summary>
    public Result SaveTileset(string owner, long? id, string name, byte[] bmp, string? roles, out long savedId)
    {
        savedId = 0;
        var display = MapDisplayName(name);
        if (display == "map") display = "tileset";
        var rolesOrNull = string.IsNullOrWhiteSpace(roles) ? null : roles;
        long newId = 0;
        var result = Tx(() =>
        {
            if (id is { } existing)
            {
                if (Scalar("SELECT COUNT(*) FROM pilot_tilesets WHERE id = $id AND owner = $o", ("$id", existing), ("$o", owner)) == 0)
                    return Fail("That tileset isn't in your collection.");
                Run("UPDATE pilot_tilesets SET name = $n, bmp = $b, roles = $r, updated = $t WHERE id = $id",
                    ("$n", display), ("$b", bmp), ("$r", (object?)rolesOrNull ?? DBNull.Value), ("$t", Now()), ("$id", existing));
                newId = existing;
                return Ok($"Saved {display} to your tilesets.");
            }
            if (Scalar("SELECT COUNT(*) FROM pilot_tilesets WHERE owner = $o", ("$o", owner)) >= MaxPilotTilesets)
                return Fail($"You have {MaxPilotTilesets} tilesets; remove one to add another.");
            Run("INSERT INTO pilot_tilesets(owner, name, bmp, roles, updated) VALUES($o,$n,$b,$r,$t)",
                ("$o", owner), ("$n", display), ("$b", bmp), ("$r", (object?)rolesOrNull ?? DBNull.Value), ("$t", Now()));
            newId = Scalar("SELECT last_insert_rowid()");
            return Ok($"Added {display} to your tilesets.");
        });
        savedId = newId;
        return result;
    }

    public Result RemoveTileset(string owner, long id) => Tx(() =>
    {
        var name = TilesetFileOf(owner, id)?.Name;
        if (name is null) return Fail("That tileset isn't in your collection.");
        Run("DELETE FROM pilot_tilesets WHERE id = $id", ("$id", id));
        return Ok($"Removed {name} from your tilesets.");
    });

    public Result RenameTileset(string owner, long id, string name) => Tx(() =>
    {
        if (TilesetFileOf(owner, id) is null) return Fail("That tileset isn't in your collection.");
        var display = MapDisplayName(name);
        Run("UPDATE pilot_tilesets SET name = $n WHERE id = $id", ("$n", display), ("$id", id));
        return Ok($"Renamed it {display}.");
    });

    /// <summary>A copy named "&lt;name&gt; copy", counted against the cap.</summary>
    public Result DuplicateTileset(string owner, long id, out long copyId)
    {
        copyId = 0;
        var t = TilesetFileOf(owner, id);
        if (t is null) return Fail("That tileset isn't in your collection.");
        var name = t.Value.Name.Length > MaxMapNameLength - 5 ? t.Value.Name[..(MaxMapNameLength - 5)] : t.Value.Name;
        return SaveTileset(owner, null, name + " copy", t.Value.Bmp, t.Value.Roles, out copyId);
    }
}
