using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class ArchiveFoldersTests
{
    [Fact]
    public void first_archive_uses_the_plain_name()
    {
        Assert.Equal("myarena", ArchiveFolders.NextName(Array.Empty<string>(), "myarena"));
    }

    [Fact]
    public void existing_archive_is_never_overwritten_a_suffix_is_added()
    {
        Assert.Equal("myarena-2", ArchiveFolders.NextName(new[] { "myarena" }, "myarena"));
        Assert.Equal("myarena-3", ArchiveFolders.NextName(new[] { "MYARENA", "myarena-2", "other" }, "myarena"));
        Assert.Equal("myarena-2", ArchiveFolders.NextName(new[] { "myarena", "myarena-x", "myarena-3" }, "myarena"));
    }

    [Fact]
    public void latest_picks_the_highest_suffix()
    {
        Assert.Null(ArchiveFolders.Latest(new[] { "other", "my" }, "myarena"));
        Assert.Equal("myarena", ArchiveFolders.Latest(new[] { "myarena" }, "myarena"));
        Assert.Equal("myarena-3", ArchiveFolders.Latest(new[] { "myarena", "myarena-3", "myarena-2", "myarena-x", "myarenab-9" }, "myarena"));
    }
}
