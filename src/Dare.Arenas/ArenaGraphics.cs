using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Configuration;

namespace Dare.Arenas;

// An arena's own game graphics (bombs, bullets, items, ships...): each replacement is kept as
// arenas/<arena>/graphics/<slot>.png|.bmp and all of them are packed into arenas/<arena>/arena-graphics.lvz
// (files named <slot>.bm2, which the game reads in place of its own), listed in General:LevelFiles.
// Like any graphics change, it reaches players when the arena restarts. Mainloop.
public sealed partial class ArenaOwnership
{
    private const string GraphicsFolder = "graphics";

    private string? GraphicsDirPath(string arena) =>
        ArenaPaths.Resolve(ArenasDir, arena, GraphicsFolder) is { } p ? p : null;

    private string? OwnGraphicPath(string arena, string slot)
    {
        if (GraphicsDirPath(arena) is not { } dir) return null;
        foreach (var ext in new[] { ".png", ".bmp" })
        {
            var path = Path.Combine(dir, slot + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>slot name (lowercase) -> the other .lvz in LevelFiles that replaces it (ours excluded).</summary>
    private Dictionary<string, string> GraphicsFromOtherLvz(ConfigHandle cfg, string arena)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lvz in LevelFiles.Parse(_configManager.GetStr(cfg, "General", "LevelFiles")))
        {
            if (string.Equals(lvz, GameGraphics.LvzName, StringComparison.OrdinalIgnoreCase)) continue;
            if (ArenaPaths.Resolve(ArenasDir, arena, lvz) is not { } path || !File.Exists(path)) continue;
            try
            {
                foreach (var (name, _) in LvzFile.ReadFiles(File.ReadAllBytes(path), namesOnly: true))
                {
                    var stem = Path.GetFileNameWithoutExtension(name);
                    if (GameGraphics.Find(stem) is { } slot) map[slot.Name] = lvz;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Log(LogLevel.Warn, $"[{arena}] {lvz} could not be read for its graphics list: {ex.Message}");
            }
        }
        return map;
    }

    /// <summary>The Graphics page: every slot, and where the arena's current picture comes from.</summary>
    internal object GraphicsData(ConfigHandle cfg, string arena)
    {
        var others = GraphicsFromOtherLvz(cfg, arena);
        return new
        {
            lvz = GameGraphics.LvzName,
            slots = GameGraphics.Slots.Select(s => new
            {
                name = s.Name,
                label = s.Label,
                group = s.Group,
                hint = s.Hint,
                generate = s.Generate,
                source = OwnGraphicPath(arena, s.Name) is not null ? "arena" : others.TryGetValue(s.Name, out var f) ? "lvz" : "stock",
                lvzFile = others.GetValueOrDefault(s.Name),
            }),
        };
    }

    /// <summary>The arena's current picture for a slot, from its own file or another .lvz; null means the stock one.</summary>
    internal byte[]? GraphicImage(ConfigHandle cfg, string arena, string slot)
    {
        if (OwnGraphicPath(arena, slot) is { } own) return File.ReadAllBytes(own);
        if (GraphicsFromOtherLvz(cfg, arena).TryGetValue(slot, out var lvz) && ArenaPaths.Resolve(ArenasDir, arena, lvz) is { } path)
        {
            try
            {
                return LvzFile.ReadFiles(File.ReadAllBytes(path))
                    .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f.Name), slot, StringComparison.OrdinalIgnoreCase)).Data;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Owner: replaces one slot's picture (PNG or BMP) and rebuilds the arena's graphics .lvz.</summary>
    internal Result SetGraphic(ConfigHandle cfg, string arena, string actor, string slotName, byte[] image)
    {
        if (!HasRole(arena, actor, ownerOnly: true)) return new Result(false, "Only the arena's owner can change its graphics.");
        if (LiveEditRefusal(arena) is { } locked) return new Result(false, locked);
        if (GameGraphics.Find(slotName) is not { } slot) return new Result(false, "That isn't one of the game's graphics.");
        if (!GameGraphics.TryCheckImage(image, out var ext, out _, out _, out var error)) return new Result(false, error);
        if (GraphicsDirPath(arena) is not { } dir) return new Result(false, "Your arena's folder is missing; ask staff.");
        try
        {
            Directory.CreateDirectory(dir);
            if (OwnGraphicPath(arena, slot.Name) is { } old) File.Delete(old);
            var target = Path.Combine(dir, slot.Name + ext);
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, image);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Error, $"{actor}: saving graphic {slot.Name} for [{arena}] failed: {ex.Message}");
            return new Result(false, "The picture could not be saved; staff can see the details in the log.");
        }
        var packed = RebuildGraphicsLvz(cfg, arena, actor);
        if (!packed.Ok) return packed;
        Log(LogLevel.Info, $"{actor} set {slot.Name} for [{arena}] ({image.Length} bytes).");
        return new Result(true, $"{slot.Label} saved. {packed.Message}");
    }

    internal Result ClearGraphic(ConfigHandle cfg, string arena, string actor, string slotName)
    {
        if (!HasRole(arena, actor, ownerOnly: true)) return new Result(false, "Only the arena's owner can change its graphics.");
        if (LiveEditRefusal(arena) is { } locked) return new Result(false, locked);
        if (GameGraphics.Find(slotName) is not { } slot) return new Result(false, "That isn't one of the game's graphics.");
        if (OwnGraphicPath(arena, slot.Name) is not { } path) return new Result(true, $"{slot.Label} already uses the game's own picture.");
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, "The picture could not be removed; staff can see the details in the log.");
        }
        var packed = RebuildGraphicsLvz(cfg, arena, actor);
        if (!packed.Ok) return packed;
        Log(LogLevel.Info, $"{actor} reset {slot.Name} for [{arena}].");
        return new Result(true, $"{slot.Label} is back to the game's own picture. {packed.Message}");
    }

    /// <summary>Packs every saved slot into arena-graphics.lvz and lists it in LevelFiles (or drops both when none are left).</summary>
    private Result RebuildGraphicsLvz(ConfigHandle cfg, string arena, string actor)
    {
        var lvzPath = ArenaPaths.Resolve(ArenasDir, arena, GameGraphics.LvzName);
        if (lvzPath is null) return new Result(false, "Your arena's folder is missing; ask staff.");
        var files = GameGraphics.Slots
            .Select(s => (s, path: OwnGraphicPath(arena, s.Name)))
            .Where(x => x.path is not null)
            .Select(x => (x.s.Name + ".bm2", File.ReadAllBytes(x.path!)))
            .ToList();
        var current = _configManager.GetStr(cfg, "General", "LevelFiles");
        var listed = LevelFiles.Find(current, GameGraphics.LvzName) is not null;
        MarkPendingRestart(arena, "graphics");

        if (files.Count == 0)
        {
            try { if (File.Exists(lvzPath)) File.Delete(lvzPath); } catch (IOException) { }
            if (listed && LevelFiles.Remove(current, GameGraphics.LvzName, out var without, out _, out _))
                WriteArenaSetting(cfg, arena, "General", "LevelFiles", without, $"{actor} reset the arena's graphics with Dare.Arenas");
            return new Result(true, GraphicsTakeEffect);
        }

        var lvz = LvzFile.Write(files);
        if (lvz.Length > MaxTransferBytes) return new Result(false, "All your graphics together must pack into 5 MB or less; use smaller pictures.");
        try
        {
            File.WriteAllBytes(lvzPath + ".tmp", lvz);
            File.Move(lvzPath + ".tmp", lvzPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Error, $"{actor}: writing {GameGraphics.LvzName} for [{arena}] failed: {ex.Message}");
            return new Result(false, "The graphics file could not be written; staff can see the details in the log.");
        }
        if (!listed)
        {
            if (!LevelFiles.Add(current, GameGraphics.LvzName, MaxLevelFiles, out var with, out var error))
                return new Result(false, error + " Remove a .lvz to make room for your graphics.");
            WriteArenaSetting(cfg, arena, "General", "LevelFiles", with, $"{actor} set the arena's graphics with Dare.Arenas");
        }
        return new Result(true, GraphicsTakeEffect);
    }
}
