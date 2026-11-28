using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class TutorialScriptTests
{
    private const string Sample = """
    {
      "mentor": "Vex",
      "continuumKeys": { "gun": "Ctrl" },
      "steps": [
        { "id": "hello", "act": 1, "lines": ["Hi."], "check": { "kind": "none" } },
        { "id": "turn", "act": 1, "lines": ["Turn.", "More."], "check": { "kind": "rotate" } }
      ],
      "prompts": []
    }
    """;

    private static string Embedded() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tutorial.script.json"));

    // SS.Packets.Game.Prize names Core can't reference; the plugin maps these strings to the enum.
    private static readonly string[] PrizeNames =
        ["Recharge", "Energy", "Rotation", "Stealth", "Cloak", "XRadar", "Warp", "Gun", "Bomb", "Bounce", "Thrust", "Speed",
         "Shutdown", "Multifire", "Prox", "Super", "Shield", "Shrapnel", "Antiwarp", "Repel", "Burst", "Decoy", "Thor",
         "Multiprize", "Brick", "Rocket", "Portal"];

    [Fact] public void Parse_ReadsStepsInOrder() { var s = TutorialScript.Parse(Sample); Assert.Equal(new[]{"hello","turn"}, s.Steps.Select(x => x.Id)); }
    [Fact] public void Parse_NumbersLinesGloballyInFileOrder() { var s = TutorialScript.Parse(Sample); Assert.Equal(new[]{0,1,2}, s.AllLines().Select(l => l.Index)); }
    [Fact] public void Parse_RejectsUnknownCheck() => Assert.Contains("turn", Assert.Throws<FormatException>(() => TutorialScript.Parse(Sample.Replace("rotate","spin"))).Message);
    [Fact] public void ForContinuum_FillsKeys() => Assert.Equal("Ctrl fires.", TutorialScript.ForContinuum("{gun} fires.", new Dictionary<string,string>{["gun"]="Ctrl"}));
    [Fact] public void ForContinuum_KeepsUnknownPlaceholder() => Assert.Equal("{zap}", TutorialScript.ForContinuum("{zap}", new Dictionary<string,string>()));

    private const string Spots = """
    {
      "mentor": "Vex",
      "steps": [
        { "id": "shoot", "act": 1, "warpPlayer": "460 462", "lines": ["Shoot."], "check": { "kind": "none" },
          "cast": [{ "role": "target", "seat": "in", "script": "idle", "at": "468 456" }, { "role": "swarm", "seat": "in", "script": "idle" }] }
      ]
    }
    """;

    [Fact]
    public void Parse_ReadsWarpPlayerAndCastAt_BothOptional()
    {
        var step = TutorialScript.Parse(Spots).Steps[0];
        Assert.Equal(new TileSpot(460, 462), step.WarpPlayer);
        Assert.Equal(new TileSpot(468, 456), step.Cast[0].At);
        Assert.Null(step.Cast[1].At);
        Assert.Null(TutorialScript.Parse(Sample).Steps[0].WarpPlayer);
    }

    [Theory]
    [InlineData("\"460\"")]
    [InlineData("\"460 462 1\"")]
    [InlineData("\"1024 5\"")]
    [InlineData("\"-1 5\"")]
    [InlineData("\"a b\"")]
    [InlineData("[460, 462]")]
    public void Parse_RejectsBadSpots_NamingTheStep(string bad)
    {
        Assert.Contains("'shoot' warpPlayer", Assert.Throws<FormatException>(() => TutorialScript.Parse(Spots.Replace("\"460 462\"", bad))).Message);
        Assert.Contains("'shoot' at", Assert.Throws<FormatException>(() => TutorialScript.Parse(Spots.Replace("\"468 456\"", bad))).Message);
    }

    [Fact]
    public void ShippedScript_WarpsThePlayerIntoTheRing_AndPlacesTheShootingAndSwarmBots()
    {
        var s = TutorialScript.Parse(Embedded());
        TutorialStep St(string id) => s.Steps.Single(x => x.Id == id);
        Assert.Equal(new TileSpot(460, 462), St("shoot").WarpPlayer);
        Assert.Equal(new TileSpot(460, 462), St("shutdown").WarpPlayer);
        Assert.All(St("shoot").Cast.Concat(St("shoot").LeanCast), c => Assert.Equal(new TileSpot(468, 456), c.At));
        foreach (var id in new[] { "swarm", "shields", "repel", "shootswarm", "super" })
        {
            Assert.Equal(5, St(id).Cast.Select(c => c.At).Distinct().Count(a => a is not null));
            Assert.Equal(new TileSpot(474, 462), St(id).LeanCast.Single().At);
        }
    }

    [Fact]
    public void ShippedScript_Parses_AndEveryLineFitsOneBubble()
    {
        var s = TutorialScript.Parse(Embedded());
        Assert.All(s.AllLines(), l => Assert.True(l.Text.Length <= 90, l.Text));
        Assert.Equal(20, s.Steps.Count);
    }

    [Fact]
    public void ShippedScript_LineIndexesAreContiguous_PromptsLast()
    {
        var s = TutorialScript.Parse(Embedded());
        var all = s.AllLines().ToList();
        Assert.Equal(Enumerable.Range(0, all.Count), all.Select(l => l.Index));
        Assert.Equal(s.Prompts.Select(p => p.Index), all.TakeLast(s.Prompts.Count).Select(l => l.Index));
        Assert.All(s.Prompts, p => Assert.False(string.IsNullOrEmpty(p.Id)));
        Assert.Contains(s.Prompts, p => p.Id == "skip_popup");
        Assert.Contains(s.Prompts, p => p.Id == "try_tutorial");
        Assert.Contains(s.Prompts, p => p.Id == "arena_pointer");
    }

    [Fact]
    public void ShippedScript_GrantsAreRealPrizes_AndStep12GivesSuperAndGun()
    {
        var s = TutorialScript.Parse(Embedded());
        Assert.All(s.Steps.SelectMany(x => x.Grant), g => Assert.Contains(g, PrizeNames));
        Assert.Equal(new[] { "Super", "Gun" }, s.Steps[11].Grant);
        Assert.Equal(new[] { "Shield", "Repel" }, s.Steps[8].Grant);
    }

    [Fact]
    public void ShippedScript_KeysAndPlaceholders()
    {
        var s = TutorialScript.Parse(Embedded());
        Assert.Equal("Ctrl", s.ContinuumKeys["gun"]);
        Assert.Equal("Tab", s.ContinuumKeys["bomb"]);
        Assert.Equal("Left", s.ContinuumKeys["left"]);
        Assert.Equal("Right", s.ContinuumKeys["right"]);
        Assert.Equal("Up", s.ContinuumKeys["forward"]);
        Assert.Equal("Down", s.ContinuumKeys["backward"]);
        var known = new[] { "gun", "bomb", "repel", "afterburner", "left", "right", "forward", "backward" };
        foreach (var l in s.AllLines())
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(l.Text, @"\{(\w+)\}"))
                Assert.Contains(m.Groups[1].Value, known);
        Assert.DoesNotContain("{", string.Join(" ", s.AllLines().Select(l => TutorialScript.ForContinuum(l.Text, s.ContinuumKeys))));
    }

    [Fact]
    public void ShippedScript_StepDetails()
    {
        var s = TutorialScript.Parse(Embedded());
        Assert.True(s.Steps[6].EngineOff);
        Assert.Equal(new[] { 3, 6, 8, 12 }, s.Steps.Select((x, i) => (x, i)).Where(t => t.x.PauseSeconds == 4).Select(t => t.i));
        Assert.Equal(CheckKind.Kills, s.Steps[4].Check.Kind);
        Assert.Equal(1, s.Steps[4].Check.Count);
        Assert.Equal(CheckKind.Kills, s.Steps[10].Check.Kind);
        Assert.Equal(1, s.Steps[10].Check.Count);
        Assert.Equal(5, s.Steps[10].Check.OrAfterSeconds);
        Assert.Equal(5, s.Steps[7].Cast.Count(c => c.Role == "swarm"));
        Assert.Single(s.Steps[7].LeanCast);
    }
}
