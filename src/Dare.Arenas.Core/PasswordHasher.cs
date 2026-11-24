using System.Security.Cryptography;
using System.Text;

namespace Dare.Arenas.Core;

/// <summary>PBKDF2 password hashing for accounts, plus the zone's legacy passwd.conf hash for imported users.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000, SaltBytes = 16, HashBytes = 32;

    /// <summary>The server's StringUtils.DefaultEncoding (Windows-1252), which AuthFile uses for the legacy hash.</summary>
    private static readonly Encoding LegacyEncoding = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    // One global cap on concurrent PBKDF2 runs (game and web), so a flood of logins can't use every core.
    private static SemaphoreSlim _slots = new(Environment.ProcessorCount);
    private static readonly AsyncLocal<TimeSpan?> MaxWait = new();
    private static int _current, _peak;
    [ThreadStatic] private static int _threadCalls;

    /// <summary>Thrown when a caller with a <see cref="WaitLimit"/> could not get a PBKDF2 slot in time.</summary>
    public sealed class BusyException : Exception
    {
        public BusyException() : base("Server busy, try again.") { }
    }

    /// <summary>PBKDF2 runs started on the current thread (diagnostics, used by tests).</summary>
    public static int ThreadHashCalls => _threadCalls;

    /// <summary>Highest number of PBKDF2 runs seen at once since <see cref="ResetPeak"/>.</summary>
    public static int PeakConcurrent => Volatile.Read(ref _peak);

    public static void ResetPeak() => Volatile.Write(ref _peak, 0);

    /// <summary>Sets the number of PBKDF2 slots (default: processor count). Call at startup (or in tests), not under load.</summary>
    public static void Configure(int slots) => _slots = new SemaphoreSlim(Math.Max(1, slots));

    /// <summary>Within this scope (and the async calls it flows into) waiting for a slot gives up after <paramref name="limit"/> with <see cref="BusyException"/>. Without it callers queue.</summary>
    public static IDisposable WaitLimit(TimeSpan limit)
    {
        var previous = MaxWait.Value;
        MaxWait.Value = limit;
        return new Scope(() => MaxWait.Value = previous);
    }

    /// <summary>Takes a slot until disposed (tests).</summary>
    public static IDisposable Occupy()
    {
        var sem = _slots;
        sem.Wait();
        return new Scope(() => sem.Release());
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) onDispose(); }
    }

    private static byte[] Derive(byte[] password, byte[] salt, int iterations, int length)
    {
        var sem = _slots;
        if (MaxWait.Value is { } limit)
        {
            if (!sem.Wait(limit)) throw new BusyException();
        }
        else sem.Wait();
        try
        {
            var now = Interlocked.Increment(ref _current);
            int peak;
            while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }
            _threadCalls++;
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
            sem.Release();
        }
    }

    public static (string Hash, string Salt, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(Encoding.UTF8.GetBytes(password), salt, Iterations, HashBytes);
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
        var actual = Derive(Encoding.UTF8.GetBytes(password), saltBytes, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Matches AuthFile.TryGetPasswordHash: SHA-512 over a 56-byte buffer (lower-cased name &lt;=23 chars at 0, password &lt;=31 chars at 24), Base64.</summary>
    public static string LegacyHash(string name, string password)
    {
        if (name.Length > 23) name = name[..23];
        if (password.Length > 31) password = password[..31];
        var data = new byte[56];
        LegacyEncoding.GetBytes(name.ToLowerInvariant()).CopyTo(data, 0);
        LegacyEncoding.GetBytes(password).CopyTo(data, 24);
        return Convert.ToBase64String(SHA512.HashData(data));
    }

    public static bool VerifyLegacy(string name, string password, string legacyHash)
    {
        if (string.IsNullOrEmpty(legacyHash)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(LegacyHash(name, password)), Encoding.ASCII.GetBytes(legacyHash));
    }
}
