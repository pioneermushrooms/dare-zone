using System.Text.RegularExpressions;
using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public sealed class EmailMessagesTests
{
    [Theory]
    [InlineData(CodePurpose.Signup)]
    [InlineData(CodePurpose.Recovery)]
    [InlineData(CodePurpose.AddEmail)]
    public void each_purpose_carries_the_code_in_text_and_html(CodePurpose purpose)
    {
        var (subject, text, html) = EmailMessages.Code(purpose, "042917", "Big Chris");
        Assert.False(string.IsNullOrWhiteSpace(subject));
        Assert.DoesNotContain("042917", subject);
        Assert.Contains("042917", text);
        Assert.Contains("042917", html);
        Assert.Contains("10 minutes", text);
    }

    [Theory]
    [InlineData(CodePurpose.Signup)]
    [InlineData(CodePurpose.Recovery)]
    [InlineData(CodePurpose.AddEmail)]
    public void no_link_carries_the_code(CodePurpose purpose)
    {
        var (_, text, html) = EmailMessages.Code(purpose, "042917", "Big Chris");
        foreach (Match m in Regex.Matches(text + " " + html, @"(https?://|www\.)[^\s""'<>]*", RegexOptions.IgnoreCase))
            Assert.DoesNotContain("042917", m.Value);
        foreach (Match m in Regex.Matches(html, @"href\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase))
            Assert.DoesNotContain("042917", m.Groups[1].Value);
    }

    [Fact]
    public void code_is_in_large_type_in_html()
    {
        var (_, _, html) = EmailMessages.Code(CodePurpose.Signup, "042917", "Big Chris");
        Assert.Matches(@"font-size:\s*(2[4-9]|[3-9][0-9])px[^>]*>042917<", html);
    }

    [Fact]
    public void name_is_html_encoded()
    {
        var (_, text, html) = EmailMessages.Code(CodePurpose.Recovery, "123456", "<b>x&y</b>");
        Assert.DoesNotContain("<b>x&y</b>", html);
        Assert.Contains("&lt;b&gt;x&amp;y&lt;/b&gt;", html);
        Assert.Contains("<b>x&y</b>", text);
    }

    [Fact]
    public void null_name_is_fine()
    {
        var (_, text, html) = EmailMessages.Code(CodePurpose.AddEmail, "123456", null);
        Assert.Contains("123456", text);
        Assert.Contains("123456", html);
    }

    [Theory]
    [InlineData("mike@x.com", "m***@x.com")]
    [InlineData("a@b.org", "a***@b.org")]
    [InlineData("not-an-email", "***")]
    [InlineData("", "***")]
    public void mask_hides_the_local_part(string email, string expected)
    {
        Assert.Equal(expected, EmailMessages.Mask(email));
    }

    [Fact]
    public void recovery_for_several_pilots_says_to_type_the_name()
    {
        var (_, text, html) = EmailMessages.Code(CodePurpose.Recovery, "123456", "dare or daretest", pilots: 2);
        Assert.Contains("for dare or daretest", text);
        Assert.Contains("type the pilot name", text);
        Assert.Contains("type the pilot name", html);
        Assert.DoesNotContain("type the pilot name", EmailMessages.Code(CodePurpose.Recovery, "123456", "dare").Text);
        // one pilot whose name happens to contain " or "
        Assert.DoesNotContain("type the pilot name", EmailMessages.Code(CodePurpose.Recovery, "123456", "Rock or Bust", pilots: 1).Text);
    }
}
