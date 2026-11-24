using System.Security.Cryptography;
using System.Text;
using Dare.Arenas.Core;
using Xunit;

namespace Dare.Arenas.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_round_trips()
    {
        var (hash, salt, iterations) = PasswordHasher.Hash("hunter22!");
        Assert.Equal(210_000, iterations);
        Assert.Equal(32, Convert.FromBase64String(hash).Length);
        Assert.Equal(16, Convert.FromBase64String(salt).Length);
        Assert.True(PasswordHasher.Verify("hunter22!", hash, salt, iterations));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var (hash, salt, iterations) = PasswordHasher.Hash("hunter22!");
        Assert.False(PasswordHasher.Verify("hunter23!", hash, salt, iterations));
    }

    [Fact]
    public void Same_password_hashes_differ_by_salt()
    {
        var a = PasswordHasher.Hash("hunter22!");
        var b = PasswordHasher.Hash("hunter22!");
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Verify_rejects_malformed_stored_values()
    {
        Assert.False(PasswordHasher.Verify("x", "not base64!", "also not", 210_000));
        Assert.False(PasswordHasher.Verify("x", "", "", 0));
    }

    // Independent computation of AuthFile.TryGetPasswordHash (SHA512 + Base64).
    private static string Expected(string name, string pw)
    {
        var data = new byte[56];
        var n = name.Length > 23 ? name[..23] : name;
        var p = pw.Length > 31 ? pw[..31] : pw;
        Encoding.Latin1.GetBytes(n.ToLowerInvariant()).CopyTo(data, 0);
        Encoding.Latin1.GetBytes(p).CopyTo(data, 24);
        return Convert.ToBase64String(SHA512.HashData(data));
    }

    [Fact]
    public void legacy_hash_truncates_like_zone()
    {
        Assert.Equal(Expected("Dare", "secret123"), PasswordHasher.LegacyHash("Dare", "secret123"));
        Assert.Equal(PasswordHasher.LegacyHash("dare", "secret123"), PasswordHasher.LegacyHash("DARE", "secret123"));

        var longName = new string('n', 30);
        var longPw = new string('p', 40);
        Assert.Equal(PasswordHasher.LegacyHash(new string('n', 23), new string('p', 31)),
                     PasswordHasher.LegacyHash(longName, longPw));
        Assert.Equal(Expected(longName, longPw), PasswordHasher.LegacyHash(longName, longPw));
    }

    [Fact]
    public void VerifyLegacy_matches_and_rejects()
    {
        var h = PasswordHasher.LegacyHash("Dare", "secret123");
        Assert.True(PasswordHasher.VerifyLegacy("DARE", "secret123", h));
        Assert.False(PasswordHasher.VerifyLegacy("Dare", "secret124", h));
        Assert.False(PasswordHasher.VerifyLegacy("Dare", "secret123", ""));
    }
}
