using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class TutorialRunTests
{
    private static readonly CastLine Target = new("target", "in", "idle");
    private static readonly CastLine Swarm = new("swarm", "in", "chase");
    private static readonly CastLine Mate = new("teammate", "spec", "follow");

    private static TutorialLine L(int i) => new(i, $"line {i}");

    private static TutorialStep Step(string id, int act, TutorialCheck check, int pause = 0, int lines = 1,
        HudPart[]? reveal = null, string[]? grant = null, bool engineOff = false, CastLine[]? cast = null, CastLine[]? lean = null) =>
        new(id, act, Enumerable.Range(0, lines).Select(L).ToList(), reveal ?? [], grant ?? [], engineOff, check, pause, cast ?? [], lean ?? []);

    private static TutorialScript Script(params TutorialStep[] steps) =>
        new("Vex", new Dictionary<string, string>(), steps, []);

    private static TutorialScript FourSteps() => Script(
        Step("turn", 1, new(CheckKind.Rotate), lines: 2, reveal: [HudPart.Radar], grant: ["Gun"], engineOff: true, cast: [Target], lean: [Mate]),
        Step("shoot", 1, new(CheckKind.Weapon, Weapon: "bullet")),
        Step("kills", 2, new(CheckKind.Kills, Count: 3, OrAfterSeconds: 20)),
        Step("rest", 2, new(CheckKind.None), pause: 5));

    private sealed class Clock { public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); public void Add(int s) => Now = Now.AddSeconds(s); }

    private static (TutorialRun Run, Clock Clock) Run(TutorialScript? s = null, bool lean = false, int startStep = 0)
    {
        var clock = new Clock();
        return (new TutorialRun(s ?? FourSteps(), () => clock.Now, lean, startStep), clock);
    }

    private static (TutorialRun Run, Clock Clock) Started(TutorialScript? s = null, bool lean = false, int startStep = 0)
    {
        var r = Run(s, lean, startStep);
        r.Run.Start();
        return r;
    }

    private static void Turn(TutorialRun run)
    {
        for (var r = 0; r < 40; r += 5) run.Handle(new Rotated(r));
        run.Handle(new Rotated(0));
    }

    [Fact] public void Start_SaysFirstLines_WithStepCount()
    {
        var fx = Run().Run.Start();
        Assert.Equal(2, fx.OfType<Say>().Count());
        Assert.All(fx.OfType<Say>(), s => { Assert.Equal(1, s.Step); Assert.Equal(4, s.Of); });
    }

    [Fact] public void RotateStep_NeedsAFullTurn()
    {
        var (run, _) = Started();
        for (var r = 0; r < 20; r += 5) run.Handle(new Rotated(r));
        Assert.Equal(0, run.StepIndex);
        Turn(run);
        Assert.Equal(1, run.StepIndex);
    }

    [Fact] public void RotateStep_CountsEitherDirection_AcrossTheWrap()
    {
        var (run, _) = Started();
        for (var r = 35; r > 35 - 40; r -= 5) run.Handle(new Rotated(((r % 40) + 40) % 40));
        Assert.Equal(0, run.StepIndex); // 35 steps of turning so far
        run.Handle(new Rotated(35));
        Assert.Equal(1, run.StepIndex);
    }

    [Fact] public void RotateStep_FirstRotationOnlySetsBaseline()
    {
        var (run, _) = Started();
        run.Handle(new Rotated(30));
        run.Handle(new Rotated(30));
        Assert.Equal(0, run.StepIndex);
    }

    [Fact] public void WeaponStep_IgnoresOtherWeapons()
    {
        var (run, _) = Started(startStep: 1);
        run.Handle(new Fired("bomb"));
        Assert.Equal(1, run.StepIndex);
        run.Handle(new Fired("bullet"));
        Assert.Equal(2, run.StepIndex);
    }

    [Fact] public void KillsStep_AdvancesAtCount_AnyRole()
    {
        var (run, _) = Started(startStep: 2);
        run.Handle(new Killed("swarm")); run.Handle(new Killed("target"));
        Assert.Equal(2, run.StepIndex);
        run.Handle(new Killed("swarm"));
        Assert.Equal(3, run.StepIndex);
    }

    [Fact] public void KillsStep_AdvancesOnTimerWithoutKills()
    {
        var (run, clock) = Started(startStep: 2);
        clock.Add(19); run.Handle(new Tick());
        Assert.Equal(2, run.StepIndex);
        clock.Add(2); run.Handle(new Tick());
        Assert.Equal(3, run.StepIndex);
    }

    [Fact] public void KillAll_Lean_NeedsOneKill()
    {
        var s = Script(Step("k", 1, new(CheckKind.KillAll), cast: [Swarm, Swarm, Swarm], lean: [Swarm]), Step("n", 1, new(CheckKind.None)));
        var (run, _) = Started(s, lean: true);
        run.Handle(new Killed("swarm"));
        Assert.Equal(1, run.StepIndex);
    }

    [Fact] public void KillAll_Full_NeedsEverySwarmBot()
    {
        var s = Script(Step("k", 1, new(CheckKind.KillAll), cast: [Target, Swarm, Swarm, Swarm, Swarm, Swarm]), Step("n", 1, new(CheckKind.None)));
        var (run, _) = Started(s);
        for (var i = 0; i < 4; i++) run.Handle(new Killed("swarm"));
        run.Handle(new Killed("target"));
        Assert.Equal(0, run.StepIndex);
        run.Handle(new Killed("swarm"));
        Assert.Equal(1, run.StepIndex);
    }

    [Fact] public void PauseStep_AdvancesAfterPause()
    {
        var (run, clock) = Started(startStep: 3);
        clock.Add(4); run.Handle(new Tick());
        Assert.False(run.Done);
        clock.Add(1); run.Handle(new Tick());
        Assert.True(run.Done);
    }

    [Fact] public void PauseZero_AdvancesOnNextTick()
    {
        var s = Script(Step("a", 1, new(CheckKind.None)), Step("b", 1, new(CheckKind.Rotate)));
        var (run, _) = Started(s);
        Assert.Equal(0, run.StepIndex);
        run.Handle(new Tick());
        Assert.Equal(1, run.StepIndex);
    }

    [Theory]
    [InlineData(CheckKind.SafeZone)] [InlineData(CheckKind.Green)] [InlineData(CheckKind.PublicChat)]
    [InlineData(CheckKind.ShipChange)] [InlineData(CheckKind.Flag)] [InlineData(CheckKind.BallGoal)]
    public void EventChecks_AdvanceOnTheirOwnEventOnly(CheckKind kind)
    {
        TutorialEvent mine = kind switch
        {
            CheckKind.SafeZone => new EnteredSafe(), CheckKind.Green => new PickedGreen(), CheckKind.PublicChat => new SaidPublic(),
            CheckKind.ShipChange => new ChangedShip(3), CheckKind.Flag => new GotFlag(), _ => new ScoredGoal(),
        };
        var s = Script(Step("a", 1, new(kind)), Step("b", 1, new(CheckKind.Rotate)));
        var (run, _) = Started(s);
        foreach (TutorialEvent other in new TutorialEvent[] { new EnteredSafe(), new PickedGreen(), new SaidPublic(), new ChangedShip(3), new GotFlag(), new ScoredGoal(), new Tick() })
            if (other.GetType() != mine.GetType()) run.Handle(other);
        Assert.Equal(0, run.StepIndex);
        run.Handle(mine);
        Assert.Equal(1, run.StepIndex);
    }

    [Fact] public void ReachStep_AdvancesWithinRadiusTiles()
    {
        var s = Script(Step("r", 1, new(CheckKind.Reach, X: 100, Y: 100, Radius: 5)), Step("n", 1, new(CheckKind.Rotate)));
        var (run, _) = Started(s);
        run.Handle(new Moved(106, 100));
        run.Handle(new Moved(104, 104)); // 5.66 away
        Assert.Equal(0, run.StepIndex);
        run.Handle(new Moved(103, 103)); // 4.24 away
        Assert.Equal(1, run.StepIndex);
    }

    [Fact] public void Advance_EmitsRevealGrantEngineCastThenLines()
    {
        var fx = Run().Run.Start();
        Assert.Equal(new[] { typeof(RevealPart), typeof(Grant), typeof(EngineOff), typeof(SetCast), typeof(Say), typeof(Say) }, fx.Select(f => f.GetType()));
        Assert.Equal(HudPart.Radar, ((RevealPart)fx[0]).Part);
        Assert.Equal("Gun", ((Grant)fx[1]).Prize);
    }

    [Fact] public void Advance_EmitsEngineOn_WhenStepIsNotEngineOff_AndSetCastEveryStep()
    {
        var (run, _) = Started();
        var fx = run.Handle(new Skip());
        Assert.Contains(fx, e => e is EngineOn);
        Assert.DoesNotContain(fx, e => e is EngineOff);
        Assert.Empty(((SetCast)fx.Single(e => e is SetCast)).Cast);
    }

    [Fact] public void Lean_UsesLeanCast()
    {
        Assert.Equal(new[] { Mate }, ((SetCast)Run(lean: true).Run.Start().Single(e => e is SetCast)).Cast);
        Assert.Equal(new[] { Target }, ((SetCast)Run().Run.Start().Single(e => e is SetCast)).Cast);
    }

    [Fact] public void NoProgress_For60s_ShowsSkipPromptOnce_ThenEvery60s()
    {
        var (run, clock) = Started();
        clock.Add(59); Assert.Empty(run.Handle(new Tick()));
        clock.Add(1); Assert.Single(run.Handle(new Tick()), e => e is ShowSkipPrompt);
        clock.Add(30); Assert.Empty(run.Handle(new Tick()));
        clock.Add(30); Assert.Single(run.Handle(new Tick()), e => e is ShowSkipPrompt);
    }

    [Fact] public void SkipPromptTimer_RestartsOnEveryStep()
    {
        var (run, clock) = Started();
        clock.Add(50); run.Handle(new Skip());
        clock.Add(50); Assert.Empty(run.Handle(new Tick()));
        clock.Add(10); Assert.Single(run.Handle(new Tick()), e => e is ShowSkipPrompt);
    }

    [Fact] public void Skip_AlwaysAdvances()
    {
        var s = Script(
            Step("rot", 1, new(CheckKind.Rotate)), Step("wpn", 1, new(CheckKind.Weapon, Weapon: "mine")), Step("ka", 1, new(CheckKind.KillAll)),
            Step("k", 1, new(CheckKind.Kills, Count: 2)), Step("none", 1, new(CheckKind.None), pause: 99), Step("flag", 1, new(CheckKind.Flag)));
        var (run, _) = Started(s);
        for (var i = 1; i <= 5; i++) { run.Handle(new Skip()); Assert.Equal(i, run.StepIndex); }
    }

    [Fact] public void Death_DoesNotResetStep()
    {
        var (run, _) = Started(startStep: 1);
        run.Handle(new Died());
        Assert.Equal(1, run.StepIndex);
    }

    [Fact] public void LastStep_EmitsFinished_AndDone_ThenIgnoresEverything()
    {
        var (run, clock) = Started(startStep: 3);
        clock.Add(5);
        var fx = run.Handle(new Tick());
        Assert.Contains(fx, e => e is Finished);
        Assert.True(run.Done);
        clock.Add(500);
        Assert.Empty(run.Handle(new Tick()));
        Assert.Empty(run.Handle(new Skip()));
    }

    [Fact] public void Skip_OnLastStep_Finishes()
    {
        var (run, _) = Started(startStep: 3);
        Assert.Contains(run.Handle(new Skip()), e => e is Finished);
        Assert.True(run.Done);
    }

    [Fact] public void StartAtAct_BeginsAtThatActsFirstStep()
    {
        var s = FourSteps();
        Assert.Equal(2, TutorialRun.FirstStepOfAct(s, 2));
        var (run, _) = (new TutorialRun(s, () => DateTimeOffset.UnixEpoch, false, TutorialRun.FirstStepOfAct(s, 2)), 0);
        var fx = run.Start();
        Assert.Equal(2, run.StepIndex);
        Assert.All(fx.OfType<Say>(), say => Assert.Equal(3, say.Step));
        Assert.Equal(2, run.ActOf(run.StepIndex));
    }

    [Fact] public void FirstStepOfAct_IsMinusOne_WhenNoSuchAct() => Assert.Equal(-1, TutorialRun.FirstStepOfAct(FourSteps(), 9));
}
