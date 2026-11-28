namespace Dare.Arenas.Core;

/// <summary>Something the player (or the clock) did that the tutorial may care about.</summary>
public abstract record TutorialEvent;
/// <summary>The player's ship rotation, 0-39.</summary>
public sealed record Rotated(int Rotation) : TutorialEvent;
public sealed record Moved(int TileX, int TileY) : TutorialEvent;
public sealed record Fired(string Weapon) : TutorialEvent;
/// <summary>The player killed a bot of this cast role (target|swarm|teammate).</summary>
public sealed record Killed(string Role) : TutorialEvent;
public sealed record EnteredSafe : TutorialEvent;
public sealed record PickedGreen : TutorialEvent;
public sealed record SaidPublic : TutorialEvent;
public sealed record ChangedShip(int Ship) : TutorialEvent;
public sealed record GotFlag : TutorialEvent;
public sealed record ScoredGoal : TutorialEvent;
public sealed record Died : TutorialEvent;
public sealed record Skip : TutorialEvent;
public sealed record Tick : TutorialEvent;

/// <summary>What the zone should do as a result of an event. The zone applies these; the machine never touches the server.</summary>
public abstract record TutorialEffect;
public sealed record Say(TutorialLine Line, int Step, int Of) : TutorialEffect;
public sealed record RevealPart(HudPart Part) : TutorialEffect;
public sealed record Grant(string Prize) : TutorialEffect;
public sealed record EngineOff : TutorialEffect;
public sealed record EngineOn : TutorialEffect;
/// <summary>The full set of bots that should exist now; the zone diffs it against what is running.</summary>
public sealed record SetCast(IReadOnlyList<CastLine> Cast) : TutorialEffect;
public sealed record ShowSkipPrompt : TutorialEffect;
public sealed record Finished : TutorialEffect;

/// <summary>
/// One player's walk through a <see cref="TutorialScript"/>. Pure: feed it events, apply the effects it returns.
/// Dying never rewinds a step. Reach checks use Euclidean tile distance.
/// </summary>
public sealed class TutorialRun(TutorialScript script, Func<DateTimeOffset> clock, bool lean, int startStep = 0)
{
    private static readonly TimeSpan SkipPromptEvery = TimeSpan.FromSeconds(60);
    private const int FullTurn = 40;

    private DateTimeOffset _startedAt;
    private DateTimeOffset _nextPrompt;
    private int _kills;
    private int _swarmKills;
    private int? _lastRotation;
    private int _turned;

    public int StepIndex { get; private set; } = startStep;
    public bool Done { get; private set; }

    public int ActOf(int stepIndex) => script.Steps[stepIndex].Act;

    /// <summary>Index of the first step of the act, or -1 when the script has no such act.</summary>
    public static int FirstStepOfAct(TutorialScript s, int act)
    {
        for (var i = 0; i < s.Steps.Count; i++) if (s.Steps[i].Act == act) return i;
        return -1;
    }

    public IReadOnlyList<TutorialEffect> Start() => Enter();

    public IReadOnlyList<TutorialEffect> Handle(TutorialEvent e)
    {
        if (Done) return [];
        if (e is Skip) return Advance();

        var step = script.Steps[StepIndex];
        if (IsDone(step.Check, e)) return Advance();

        if (e is Tick && clock() >= _nextPrompt)
        {
            while (clock() >= _nextPrompt) _nextPrompt += SkipPromptEvery;
            return [new ShowSkipPrompt()];
        }
        return [];
    }

    private bool IsDone(TutorialCheck c, TutorialEvent e)
    {
        var elapsed = clock() - _startedAt;
        switch (c.Kind)
        {
            case CheckKind.None:
                return e is Tick && elapsed >= TimeSpan.FromSeconds(script.Steps[StepIndex].PauseSeconds);
            case CheckKind.Rotate:
                if (e is Rotated r) Turn(r.Rotation);
                return _turned >= FullTurn;
            case CheckKind.Reach:
                return e is Moved m && (long)(m.TileX - c.X) * (m.TileX - c.X) + (long)(m.TileY - c.Y) * (m.TileY - c.Y) <= (long)c.Radius * c.Radius;
            case CheckKind.Weapon:
                return e is Fired f && string.Equals(f.Weapon, c.Weapon, StringComparison.OrdinalIgnoreCase);
            case CheckKind.Kills:
                if (e is Killed) _kills++;
                return _kills >= c.Count
                    || (e is Tick && c.OrAfterSeconds > 0 && elapsed >= TimeSpan.FromSeconds(c.OrAfterSeconds));
            case CheckKind.KillAll:
                if (e is Killed { Role: "swarm" }) _swarmKills++;
                var need = SwarmNeeded();
                return need > 0 && _swarmKills >= need;
            case CheckKind.SafeZone: return e is EnteredSafe;
            case CheckKind.Green: return e is PickedGreen;
            case CheckKind.PublicChat: return e is SaidPublic;
            case CheckKind.ShipChange: return e is ChangedShip;
            case CheckKind.Flag: return e is GotFlag;
            case CheckKind.BallGoal: return e is ScoredGoal;
            default: return false;
        }
    }

    private int SwarmNeeded()
    {
        var step = script.Steps[StepIndex];
        return lean ? Math.Min(1, step.LeanCast.Count(c => c.Role == "swarm")) : step.Cast.Count(c => c.Role == "swarm");
    }

    // Shortest-way turning between consecutive readings (0-39 wraps); the first reading is only a baseline.
    private void Turn(int rotation)
    {
        if (_lastRotation is int last)
            _turned += Math.Abs(((rotation - last + FullTurn / 2) % FullTurn + FullTurn) % FullTurn - FullTurn / 2);
        _lastRotation = rotation;
    }

    private IReadOnlyList<TutorialEffect> Advance()
    {
        StepIndex++;
        return Enter();
    }

    private IReadOnlyList<TutorialEffect> Enter()
    {
        if (StepIndex >= script.Steps.Count)
        {
            Done = true;
            return [new Finished()];
        }
        _startedAt = clock();
        _nextPrompt = _startedAt + SkipPromptEvery;
        _kills = _swarmKills = _turned = 0;
        _lastRotation = null;

        var step = script.Steps[StepIndex];
        var fx = new List<TutorialEffect>();
        fx.AddRange(step.Reveal.Select(p => new RevealPart(p)));
        fx.AddRange(step.Grant.Select(g => new Grant(g)));
        fx.Add(step.EngineOff ? new EngineOff() : new EngineOn());
        fx.Add(new SetCast(lean ? step.LeanCast : step.Cast));
        fx.AddRange(step.Lines.Select(l => new Say(l, StepIndex + 1, script.Steps.Count)));
        return fx;
    }
}
