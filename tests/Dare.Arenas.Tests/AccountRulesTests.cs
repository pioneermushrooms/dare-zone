using Dare.Arenas.Core;
using Xunit;

namespace Dare.Arenas.Tests;

public class AccountRulesTests
{
    [Theory]
    [InlineData("dare")]
    [InlineData("Dare Devil")]
    [InlineData("a_b-c.d")]
    [InlineData("abc")]
    [InlineData("Abcdefghij1234567890")]
    public void Valid_names_are_accepted_with_original_casing(string input)
    {
        Assert.True(AccountRules.TryName(input, out var name, out var error));
        Assert.Equal(input, name);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData(" dare")]
    [InlineData("dare ")]
    [InlineData("a  b")]
    [InlineData("1abc")]
    [InlineData("_abc")]
    [InlineData("abc!")]
    [InlineData("abcdefghij12345678901")]
    [InlineData("café")]
    public void Invalid_names_are_rejected_with_a_message(string input)
    {
        Assert.False(AccountRules.TryName(input, out var name, out var error));
        Assert.Equal("", name);
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(31, true)]
    [InlineData(32, false)]
    public void Password_length_bounds(int length, bool ok)
    {
        Assert.Equal(ok, AccountRules.TryPassword(new string('x', length), out var error));
        Assert.Equal(ok, error == "");
    }

    [Theory]
    [InlineData("pässword1")]
    [InlineData("pass\tword1")]
    [InlineData("pass\u0007word1")]
    public void Password_must_be_printable_ascii(string pw)
    {
        Assert.False(AccountRules.TryPassword(pw, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Password_with_spaces_and_symbols_is_ok() =>
        Assert.True(AccountRules.TryPassword("correct horse ~!", out _));

    [Fact]
    public void Email_is_trimmed_and_lowercased()
    {
        Assert.True(AccountRules.TryEmail(" Me@X.com ", out var n, out var error));
        Assert.Equal("me@x.com", n);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("a@b")]
    [InlineData("@x.com")]
    [InlineData("a@@x.com")]
    [InlineData("a@b@x.com")]
    [InlineData("a@.com")]
    [InlineData("a@x.")]
    [InlineData("no-at.com")]
    [InlineData("a b@x.com")]
    [InlineData("")]
    public void Bad_emails_are_rejected(string input)
    {
        Assert.False(AccountRules.TryEmail(input, out var n, out var error));
        Assert.Equal("", n);
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Email_over_254_chars_is_rejected()
    {
        var email = new string('a', 250) + "@x.com";
        Assert.False(AccountRules.TryEmail(email, out _, out _));
    }
}
