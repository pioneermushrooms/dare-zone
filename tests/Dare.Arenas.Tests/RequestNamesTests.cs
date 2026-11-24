using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class RequestNamesTests
{
    private static readonly string[] Pending = { "yomama", "skyrace" };

    [Fact]
    public void approve_name_ignores_spaces_quotes_and_case()
    {
        Assert.Equal("yomama", RequestNames.Find("yo mama", Pending));
        Assert.Equal("yomama", RequestNames.Find("'Yo Mama'", Pending));
        Assert.Equal("yomama", RequestNames.Find("\"yo  mama\"", Pending));
        Assert.Equal("skyrace", RequestNames.Find("SKYRACE", Pending));
    }

    [Fact]
    public void a_name_the_server_turned_spaces_into_x_is_only_suggested()
    {
        // ?go yo mama makes the server visit "yoxmama"; people then type that. Guessing could
        // approve the wrong request ("taxi" when "tai" is pending), so it is only suggested.
        Assert.Null(RequestNames.Find("yoxmama", Pending));
        Assert.Equal("yomama", RequestNames.Suggest("yoxmama", Pending));
        Assert.Null(RequestNames.Suggest("yoxxmama", Pending));
        Assert.Equal("No pending request for 'yoxmama'. Did you mean 'yomama'? Pending: yomama, skyrace.",
            RequestNames.NotFound("yoxmama", Pending));
    }

    [Fact]
    public void unknown_names_find_nothing()
    {
        Assert.Null(RequestNames.Find("yo", Pending));
        Assert.Null(RequestNames.Find("", Pending));
    }

    [Fact]
    public void decline_splits_a_spaced_name_from_its_reason()
    {
        Assert.Equal(("yomama", "needs a map"), RequestNames.SplitNameAndReason("yo mama needs a map", Pending));
        Assert.Equal(("skyrace", "too fast"), RequestNames.SplitNameAndReason("skyrace too fast", Pending));
        Assert.Equal(("unknown", "why"), RequestNames.SplitNameAndReason("unknown why", Pending));
        Assert.Equal(("yomama", ""), RequestNames.SplitNameAndReason("yo mama", Pending));
    }

    [Fact]
    public void not_found_message_lists_what_is_pending()
    {
        Assert.Equal("No pending request for 'yo'. Pending: yomama, skyrace.", RequestNames.NotFound("yo", Pending));
        Assert.Equal("No pending request for 'yo'. Nothing is pending.", RequestNames.NotFound("yo", Array.Empty<string>()));
    }
}
