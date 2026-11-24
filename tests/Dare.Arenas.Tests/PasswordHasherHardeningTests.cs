using System.Security.Cryptography;
using System.Text;
using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

[CollectionDefinition("hasher-slots", DisableParallelization = true)]
public sealed class HasherSlotsCollection { }

public class LegacyEncodingTests
{
    [Fact]
    public void legacy_hash_encodes_with_windows_1252_like_the_server()
    {
        // 0x80 is the euro sign in Windows-1252; Latin-1 would turn it into '?'
        var name = "Old Timer";
        var password = "pa€s“word";
        var data = new byte[56];
        var win = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        win.GetBytes(name.ToLowerInvariant()).CopyTo(data, 0);
        win.GetBytes(password).CopyTo(data, 24);
        var expected = Convert.ToBase64String(SHA512.HashData(data));

        Assert.Equal(expected, PasswordHasher.LegacyHash(name, password));
        Assert.True(PasswordHasher.VerifyLegacy(name, password, expected));
    }
}

[Collection("hasher-slots")]
public sealed class PasswordHasherSlotsTests : IDisposable
{
    public void Dispose() => PasswordHasher.Configure(Environment.ProcessorCount);

    [Fact]
    public void never_more_than_the_configured_number_of_pbkdf2_runs_at_once()
    {
        PasswordHasher.Configure(2);
        PasswordHasher.ResetPeak();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => PasswordHasher.Hash("hunter22!" + i));
        Assert.InRange(PasswordHasher.PeakConcurrent, 1, 2);
    }

    [Fact]
    public void a_web_wait_limit_gives_up_with_busy_when_no_slot_frees_up()
    {
        PasswordHasher.Configure(1);
        using var held = PasswordHasher.Occupy();
        using (PasswordHasher.WaitLimit(TimeSpan.FromMilliseconds(150)))
            Assert.Throws<PasswordHasher.BusyException>(() => PasswordHasher.Hash("hunter22!"));
    }

    [Fact]
    public void without_a_wait_limit_a_caller_queues_until_a_slot_frees_up()
    {
        PasswordHasher.Configure(1);
        var held = PasswordHasher.Occupy();
        var task = Task.Run(() => PasswordHasher.Hash("hunter22!"));
        Assert.False(task.Wait(200));
        held.Dispose();
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
    }
}
