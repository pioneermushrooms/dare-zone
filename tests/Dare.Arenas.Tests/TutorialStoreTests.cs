using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

public sealed class TutorialStoreTests : IDisposable
{
    private const string Pw = "hunter22pw";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-tutorial-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly List<AccountStore> _stores = new();

    private AccountStore Open()
    {
        var s = new AccountStore(_db, () => _now);
        _stores.Add(s);
        return s;
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal", _db + ".before-persons" })
            try { File.Delete(f); } catch { }
    }

    private static void SignUp(AccountStore s, string name, string email)
    {
        Assert.True(s.StartSignup(name, Pw, email, out var code).Ok);
        Assert.True(s.VerifySignup(email, code, out _).Ok);
    }

    private void Sql(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_db};Pooling=false");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Puts the db back as it was before the tutorial table existed.</summary>
    private void ForgetTutorialTable()
    {
        foreach (var s in _stores) s.Dispose();
        _stores.Clear();
        SqliteConnection.ClearAllPools();
        Sql("DROP TABLE tutorial_pilots; DELETE FROM account_meta WHERE key = 'tutorial_backfill';");
    }

    [Fact]
    public void Backfill_MarksExistingAccounts_OnFirstCreate()
    {
        var s = Open();
        SignUp(s, "Old Hand", "old@example.com");
        ForgetTutorialTable();
        var r = Open().TutorialOf("Old Hand");
        Assert.NotNull(r);
        Assert.Equal("existing", r!.Outcome);
        Assert.Null(r.FirstGameLogin);
    }

    [Fact]
    public void Backfill_RunsOnce()
    {
        var s = Open();
        SignUp(s, "Old Hand", "old@example.com");
        ForgetTutorialTable();
        Open();
        _stores[0].Dispose();
        _stores.Clear();
        SqliteConnection.ClearAllPools();
        var s2 = Open();
        SignUp(s2, "Newbie", "new@example.com");
        Assert.Null(Open().TutorialOf("Newbie"));
    }

    [Fact]
    public void NewAccountAfterBackfill_HasNoRow()
    {
        var s = Open();
        SignUp(s, "Newbie", "new@example.com");
        Assert.Null(s.TutorialOf("Newbie"));
    }

    [Fact]
    public void NoteGameLogin_InsertsOnce_KeepsFirstTime()
    {
        var s = Open();
        SignUp(s, "Newbie", "new@example.com");
        s.NoteGameLogin("Newbie");
        _now = _now.AddDays(1);
        s.NoteGameLogin("Newbie");
        var r = s.TutorialOf("Newbie")!;
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero), r.FirstGameLogin);
        Assert.Null(r.Outcome);
        Assert.Equal(0, r.Dropouts);
    }

    [Fact]
    public void SecondPilotName_IsTheSamePerson()
    {
        var s = Open();
        SignUp(s, "Newbie", "new@example.com");
        s.NoteGameLogin("Newbie");
        SignUp(s, "Newbie Two", "new@example.com");
        Assert.NotNull(s.TutorialOf("Newbie Two"));
        s.SetTutorialOutcome("Newbie Two", "finished");
        Assert.Equal("finished", s.TutorialOf("Newbie")!.Outcome);
    }

    [Fact]
    public void Progress_And_Outcome_Stick()
    {
        var s = Open();
        SignUp(s, "Newbie", "new@example.com");
        s.NoteGameLogin("Newbie");
        s.SetTutorialProgress("Newbie", 7, 2);
        var r = s.TutorialOf("Newbie")!;
        Assert.Equal(7, r.Step);
        Assert.Equal(2, r.Act);
        s.SetTutorialOutcome("Newbie", "left");
        Assert.Equal("left", s.TutorialOf("Newbie")!.Outcome);
    }

    [Fact]
    public void Dropouts_Count()
    {
        var s = Open();
        SignUp(s, "Alpha", "alpha@example.com");
        s.NoteGameLogin("Alpha");
        Assert.Equal(1, s.AddTutorialDropout("Alpha"));
        Assert.Equal(2, s.AddTutorialDropout("Alpha"));
        Assert.Equal(2, s.TutorialOf("Alpha")!.Dropouts);
    }

    [Fact]
    public void Counts_FinishedLeftAndStoppedAtStep()
    {
        var s = Open();
        foreach (var n in new[] { "Pilot1", "Pilot2", "Pilot3", "Pilot4" })
        {
            SignUp(s, n, $"{n}@example.com");
            s.NoteGameLogin(n);
        }
        s.SetTutorialOutcome("Pilot1", "finished");
        s.SetTutorialProgress("Pilot2", 4, 1); s.SetTutorialOutcome("Pilot2", "left");
        s.SetTutorialProgress("Pilot3", 4, 1); s.SetTutorialOutcome("Pilot3", "left");
        var (fin, left, at) = s.TutorialCounts();
        Assert.Equal(1, fin);
        Assert.Equal(2, left);
        Assert.Equal(2, at[4]);
    }
}
