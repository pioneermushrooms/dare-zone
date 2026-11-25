using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class DraftFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dare-draftfiles-{Guid.NewGuid():N}");
    private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public DraftFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_template"));
        File.WriteAllText(Path.Combine(_root, "_template", "arena.conf"), "[General]\nMap = smallmape.lvl\n");
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    private string P(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    private void Write(string folder, string file, string text)
    {
        Directory.CreateDirectory(P(folder));
        File.WriteAllText(P(folder, file), text);
    }

    private static readonly string[] Files = { "arena.conf", "sky.lvl", "a.lvz" };

    private void DraftAndLive()
    {
        Write("#sky", "arena.conf", "draft conf");
        Write("#sky", "sky.lvl", "draft map");
        Write("#sky", "a.lvz", "draft lvz");
        Write("sky", "arena.conf", "live conf v1");
        Write("sky", "sky.lvl", "live map v1");
        Write("sky", "old.lvz", "live old lvz");
    }

    [Fact]
    public void create_from_template_copies_and_refuses_existing()
    {
        Assert.Null(DraftFiles.CreateFromTemplate(_root, "_template", "#sky"));
        Assert.Equal("[General]\nMap = smallmape.lvl\n", File.ReadAllText(P("#sky", "arena.conf")));
        Assert.NotNull(DraftFiles.CreateFromTemplate(_root, "_template", "#sky"));
        Assert.NotNull(DraftFiles.CreateFromTemplate(_root, "_template", "../escape"));
        Assert.NotNull(DraftFiles.CreateFromTemplate(_root, "_template", "a#b"));
    }

    [Fact]
    public void copy_arena_copies_only_allow_listed_files_that_exist()
    {
        Write("#sky", "arena.conf", "c");
        Write("#sky", "a.lvz", "l");
        Write("#sky", "x.sh", "nope");
        Assert.Null(DraftFiles.CopyArena(_root, "#sky", "sky", Files));
        Assert.True(File.Exists(P("sky", "arena.conf")));
        Assert.True(File.Exists(P("sky", "a.lvz")));
        Assert.False(File.Exists(P("sky", "x.sh")));
        Assert.False(File.Exists(P("sky", "sky.lvl"))); // not in the draft: skipped
        Assert.NotNull(DraftFiles.CopyArena(_root, "#sky", "sky", Files)); // target exists
    }

    [Fact]
    public void publish_replaces_live_and_keeps_backup()
    {
        DraftAndLive();
        Assert.Null(DraftFiles.Publish(_root, "#sky", "sky", Files, _now));
        Assert.Equal("draft conf", File.ReadAllText(P("sky", "arena.conf")));
        Assert.Equal("draft map", File.ReadAllText(P("sky", "sky.lvl")));
        Assert.Equal("draft lvz", File.ReadAllText(P("sky", "a.lvz")));
        Assert.False(File.Exists(P("sky", "old.lvz")));
        var backup = P("_published", "sky", "20261124T120000Z");
        Assert.Equal("live conf v1", File.ReadAllText(Path.Combine(backup, "arena.conf")));
        Assert.Equal("live old lvz", File.ReadAllText(Path.Combine(backup, "old.lvz")));
        Assert.False(Directory.Exists(P("_published", "sky", ".staging")));
        Assert.Equal("draft conf", File.ReadAllText(P("#sky", "arena.conf"))); // the draft is untouched
    }

    [Fact]
    public void publish_keeps_three_backups()
    {
        DraftAndLive();
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(DraftFiles.Publish(_root, "#sky", "sky", Files, _now));
            _now = _now.AddMinutes(3);
        }
        Assert.Equal(3, Directory.GetDirectories(P("_published", "sky")).Length);
    }

    [Fact]
    public void publish_failure_leaves_live_untouched()
    {
        DraftAndLive();
        File.SetUnixFileMode(P("#sky", "sky.lvl"), UnixFileMode.None); // unreadable: staging fails
        Assert.NotNull(DraftFiles.Publish(_root, "#sky", "sky", Files, _now));
        Assert.Equal("live conf v1", File.ReadAllText(P("sky", "arena.conf")));
        Assert.Equal("live map v1", File.ReadAllText(P("sky", "sky.lvl")));
        Assert.True(File.Exists(P("sky", "old.lvz")));
        Assert.Empty(Directory.Exists(P("_published", "sky"))
            ? Directory.GetFileSystemEntries(P("_published", "sky")) : Array.Empty<string>());
    }

    [Fact]
    public void publish_failure_moving_live_files_restores_them()
    {
        DraftAndLive();
        // the live folder can't be written: moving its files out fails part-way
        File.SetUnixFileMode(P("sky"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.NotNull(DraftFiles.Publish(_root, "#sky", "sky", Files, _now));
        File.SetUnixFileMode(P("sky"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal("live conf v1", File.ReadAllText(P("sky", "arena.conf")));
        Assert.Equal("live map v1", File.ReadAllText(P("sky", "sky.lvl")));
        Assert.True(File.Exists(P("sky", "old.lvz")));
    }

    [Fact]
    public void rollback_restores_newest_and_steps_back()
    {
        DraftAndLive();
        Assert.Null(DraftFiles.Publish(_root, "#sky", "sky", Files, _now));
        File.WriteAllText(P("#sky", "arena.conf"), "draft conf v2");
        _now = _now.AddMinutes(3);
        Assert.Null(DraftFiles.Publish(_root, "#sky", "sky", Files, _now));
        Assert.Equal("draft conf v2", File.ReadAllText(P("sky", "arena.conf")));

        Assert.Null(DraftFiles.Rollback(_root, "sky"));
        Assert.Equal("draft conf", File.ReadAllText(P("sky", "arena.conf")));
        Assert.Null(DraftFiles.Rollback(_root, "sky"));
        Assert.Equal("live conf v1", File.ReadAllText(P("sky", "arena.conf")));
        Assert.True(File.Exists(P("sky", "old.lvz")));
        Assert.NotNull(DraftFiles.Rollback(_root, "sky")); // nothing left
    }

    [Fact]
    public void refuses_links_and_paths_outside_arenas()
    {
        Write("#sky", "arena.conf", "c");
        var outside = Path.Combine(Path.GetTempPath(), $"dare-outside-{Guid.NewGuid():N}.lvz");
        File.WriteAllText(outside, "secret");
        try
        {
            File.CreateSymbolicLink(P("#sky", "a.lvz"), outside);
            Assert.Null(DraftFiles.CopyArena(_root, "#sky", "sky", Files));
            Assert.False(File.Exists(P("sky", "a.lvz"))); // a link is never copied
            Assert.NotNull(DraftFiles.CopyArena(_root, "#sky", "../sky", Files));
            Assert.NotNull(DraftFiles.Publish(_root, "#sky", "../sky", Files, _now));
            Assert.NotNull(DraftFiles.Rollback(_root, ".."));
        }
        finally { File.Delete(outside); }
    }
}
