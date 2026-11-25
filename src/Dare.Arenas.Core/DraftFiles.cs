using System.Globalization;

namespace Dare.Arenas.Core;

/// <summary>
/// Folder work for drafts, all under the server's <c>arenas/</c> folder: make a draft from the
/// template, copy an arena's allow-listed files to a new folder, publish a draft over its live
/// arena (keeping backups in <c>_published/&lt;live&gt;/</c>) and roll a publish back. Every method
/// returns null on success, else a short reason; nothing follows links, and nothing is written
/// outside <c>arenas/&lt;name&gt;/</c>, <c>arenas/#&lt;name&gt;/</c> or <c>arenas/_published/&lt;name&gt;/</c>.
/// </summary>
public static class DraftFiles
{
    public const string PublishedDir = "_published";
    private const string Staging = ".staging";
    private const string Trash = ".rollback";

    public static string? CreateFromTemplate(string arenasRoot, string templateDir, string draft)
    {
        try
        {
            var source = Folder(arenasRoot, templateDir, allowUnderscore: true);
            var target = Folder(arenasRoot, draft);
            if (source is null || target is null) return "bad folder name";
            if (!Directory.Exists(source)) return $"template folder '{templateDir}' is missing";
            if (Directory.Exists(target)) return $"folder '{draft}' already exists";
            Directory.CreateDirectory(target);
            foreach (var file in RegularFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Copies the <paramref name="files"/> that exist in <paramref name="from"/> into a new folder <paramref name="to"/>.</summary>
    public static string? CopyArena(string arenasRoot, string from, string to, IReadOnlyList<string> files)
    {
        var source = Folder(arenasRoot, from);
        var target = Folder(arenasRoot, to);
        if (source is null || target is null) return "bad folder name";
        if (!Directory.Exists(source)) return $"folder '{from}' is missing";
        if (Directory.Exists(target)) return $"folder '{to}' already exists";
        try
        {
            Directory.CreateDirectory(target);
            foreach (var name in files)
                if (SourceFile(source, name) is { } file)
                    File.Copy(file, Path.Combine(target, name), overwrite: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(target, true); } catch { }
            return ex.Message;
        }
    }

    /// <summary>
    /// Makes <paramref name="live"/> hold exactly the draft's allow-listed files. The live files
    /// move to <c>_published/&lt;live&gt;/&lt;utc time&gt;/</c> (the newest 3 are kept). On any failure the
    /// live folder is as it was.
    /// </summary>
    public static string? Publish(string arenasRoot, string draft, string live, IReadOnlyList<string> files, DateTimeOffset now)
    {
        var source = Folder(arenasRoot, draft);
        var target = Folder(arenasRoot, live);
        var pub = target is null ? null : PublishedFolder(arenasRoot, live);
        if (source is null || target is null || pub is null) return "bad folder name";
        if (!Directory.Exists(source)) return $"folder '{draft}' is missing";
        if (!Directory.Exists(target)) return $"folder '{live}' is missing";

        var staging = Path.Combine(pub, Staging);
        var backup = Path.Combine(pub, now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        var movedOut = new List<string>();
        var movedIn = new List<string>();
        try
        {
            if (Directory.Exists(backup)) return "a publish already happened this second";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            foreach (var name in files)
                if (SourceFile(source, name) is { } file)
                    File.Copy(file, Path.Combine(staging, name));

            Directory.CreateDirectory(backup);
            foreach (var file in RegularFiles(target))
            {
                var name = Path.GetFileName(file);
                File.Move(file, Path.Combine(backup, name));
                movedOut.Add(name);
            }
            foreach (var file in RegularFiles(staging))
            {
                var name = Path.GetFileName(file);
                File.Move(file, Path.Combine(target, name));
                movedIn.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var name in movedIn) TryDo(() => File.Delete(Path.Combine(target, name)));
            foreach (var name in movedOut) TryDo(() => File.Move(Path.Combine(backup, name), Path.Combine(target, name)));
            TryDo(() => { if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup).Any()) Directory.Delete(backup); });
            TryDo(() => { if (Directory.Exists(staging)) Directory.Delete(staging, true); });
            return ex.Message;
        }

        TryDo(() => Directory.Delete(staging, true));
        foreach (var old in DraftRules.BackupsToDelete(Backups(pub)))
            TryDo(() => Directory.Delete(Path.Combine(pub, old), true));
        return null;
    }

    /// <summary>Puts the newest backup of <paramref name="live"/> back and removes that backup.</summary>
    public static string? Rollback(string arenasRoot, string live)
    {
        var target = Folder(arenasRoot, live);
        var pub = target is null ? null : PublishedFolder(arenasRoot, live);
        if (target is null || pub is null) return "bad folder name";
        var newest = Directory.Exists(pub) ? Backups(pub).OrderByDescending(n => n, StringComparer.Ordinal).FirstOrDefault() : null;
        if (newest is null) return $"no earlier version of '{live}' to go back to";
        var backup = Path.Combine(pub, newest);
        var trash = Path.Combine(pub, Trash);
        var trashed = new List<string>();
        var restored = new List<string>();
        try
        {
            if (Directory.Exists(trash)) Directory.Delete(trash, true);
            Directory.CreateDirectory(trash);
            Directory.CreateDirectory(target);
            foreach (var file in RegularFiles(target))
            {
                var name = Path.GetFileName(file);
                File.Move(file, Path.Combine(trash, name));
                trashed.Add(name);
            }
            foreach (var file in RegularFiles(backup))
            {
                var name = Path.GetFileName(file);
                File.Move(file, Path.Combine(target, name));
                restored.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var name in restored) TryDo(() => File.Move(Path.Combine(target, name), Path.Combine(backup, name)));
            foreach (var name in trashed) TryDo(() => File.Move(Path.Combine(trash, name), Path.Combine(target, name)));
            TryDo(() => Directory.Delete(trash, true));
            return ex.Message;
        }
        TryDo(() => Directory.Delete(trash, true));
        TryDo(() => Directory.Delete(backup, true));
        return null;
    }

    // ---- internals -------------------------------------------------------

    /// <summary>
    /// The full path of <c>arenas/&lt;name&gt;</c> when <paramref name="name"/> is an arena folder name
    /// (a-z and 0-9, optionally one leading '#') or, with <paramref name="allowUnderscore"/>, a
    /// fixed folder like <c>_template</c>; else null. Never a link.
    /// </summary>
    private static string? Folder(string arenasRoot, string name, bool allowUnderscore = false)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return null;
        var body = DraftRules.IsDraft(name) ? name[1..] : name;
        if (body.Length == 0) return null;
        var ok = allowUnderscore
            ? body.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            : body.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));
        if (!ok) return null;
        var root = Path.GetFullPath(arenasRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.Ordinal)) return null;
        if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is not null) return null;
        return full;
    }

    private static string? PublishedFolder(string arenasRoot, string live)
    {
        var published = Folder(arenasRoot, PublishedDir, allowUnderscore: true);
        if (published is null || DraftRules.IsDraft(live)) return null;
        if (Directory.Exists(published) && new DirectoryInfo(published).LinkTarget is not null) return null;
        var full = Path.Combine(published, live);
        if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is not null) return null;
        return full;
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/> when it is a plain existing file (not a link), else null.</summary>
    private static string? SourceFile(string folder, string name)
    {
        if (!LevelFiles.IsSafeFileName(name) && name != "arena.conf") return null;
        var file = Path.Combine(folder, name);
        var info = new FileInfo(file);
        return info.Exists && info.LinkTarget is null ? file : null;
    }

    private static IEnumerable<string> RegularFiles(string folder) =>
        Directory.EnumerateFiles(folder).Where(f => new FileInfo(f).LinkTarget is null).ToList();

    private static IEnumerable<string> Backups(string pub) =>
        Directory.EnumerateDirectories(pub).Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.StartsWith('.')).ToList();

    private static void TryDo(Action a)
    {
        try { a(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
