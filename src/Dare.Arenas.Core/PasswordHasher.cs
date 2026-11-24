using System.Security.Cryptography;
using System.Text;

namespace Dare.Arenas.Core;

/// <summary>PBKDF2 password hashing for accounts, plus the zone's legacy passwd.conf hash for imported users.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000, SaltBytes = 16, HashBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), Iterations);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        if (iterations <= 0) return false;
        byte[] expected, saltBytes;
        try
        {
            expected = Convert.FromBase64String(hash);
            saltBytes = Convert.FromBase64String(salt);
        }
        catch (FormatException)
        {
            return false;
        }
        if (expected.Length == 0 || saltBytes.Length == 0) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), saltBytes, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Matches AuthFile.TryGetPasswordHash: SHA-512 over a 56-byte buffer (lower-cased name &lt;=23 chars at 0, password &lt;=31 chars at 24), Base64.</summary>
    public static string LegacyHash(string name, string password)
    {
        if (name.Length > 23) name = name[..23];
        if (password.Length > 31) password = password[..31];
        var data = new byte[56];
        Encoding.Latin1.GetBytes(name.ToLowerInvariant()).CopyTo(data, 0);
        Encoding.Latin1.GetBytes(password).CopyTo(data, 24);
        return Convert.ToBase64String(SHA512.HashData(data));
    }

    public static bool VerifyLegacy(string name, string password, string legacyHash)
    {
        if (string.IsNullOrEmpty(legacyHash)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(LegacyHash(name, password)), Encoding.ASCII.GetBytes(legacyHash));
    }
}
