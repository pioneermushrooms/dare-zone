using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dare.Arenas.Core;

public enum HudPart { Radar, Energy, StatBox, Items, Chat }

public enum CheckKind { None, Rotate, Reach, Weapon, Kills, KillAll, SafeZone, Green, PublicChat, ShipChange, Flag, BallGoal }

/// <summary>What the server watches for before a step moves on. <see cref="Weapon"/> is bullet|bomb|mine|repel; Reach is in tiles.</summary>
public sealed record TutorialCheck(CheckKind Kind, int X = 0, int Y = 0, int Radius = 0, string? Weapon = null, int Count = 0, int OrAfterSeconds = 0);

/// <summary>A bot the step needs. Role target|swarm|teammate, Seat in|spec, Script is oss-bot script lines.</summary>
public sealed record CastLine(string Role, string Seat, string Script);

/// <summary>
/// One mentor line. <see cref="Index"/> is global in file order (steps, then prompts) and is the LVZ
/// object id offset (5000 + Index). <see cref="Id"/> is set only for prompts, a stable name the plugin looks them up by.
/// </summary>
public sealed record TutorialLine(int Index, string Text, string? Id = null);

public sealed record TutorialStep(
    string Id, int Act, IReadOnlyList<TutorialLine> Lines, IReadOnlyList<HudPart> Reveal, IReadOnlyList<string> Grant,
    bool EngineOff, TutorialCheck Check, int PauseSeconds, IReadOnlyList<CastLine> Cast, IReadOnlyList<CastLine> LeanCast);

/// <summary>
/// The in-game tutorial script (<c>tutorial/script.json</c>): steps, mentor lines, reveals, grants, checks and
/// bot casts. <see cref="Prompts"/> are the lines shown outside the step flow (skip popup, "try the tutorial", ending pointer).
/// </summary>
public sealed partial record TutorialScript(
    string MentorName, IReadOnlyDictionary<string, string> ContinuumKeys, IReadOnlyList<TutorialStep> Steps, IReadOnlyList<TutorialLine> Prompts)
{
    private static readonly string[] Weapons = ["bullet", "bomb", "mine", "repel"];

    public IEnumerable<TutorialLine> AllLines() => Steps.SelectMany(s => s.Lines).Concat(Prompts);

    /// <summary>Replaces <c>{action}</c> with the Continuum key name; unknown placeholders stay as written.</summary>
    public static string ForContinuum(string text, IReadOnlyDictionary<string, string> keys) =>
        Placeholder().Replace(text, m => keys.TryGetValue(m.Groups[1].Value, out var k) ? k : m.Value);

    public static TutorialScript Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new FormatException($"tutorial script: {ex.Message}", ex); }

        var mentor = Text(root, "mentor", "script");
        var keys = new Dictionary<string, string>();
        if (root.TryGetProperty("continuumKeys", out var k) && k.ValueKind == JsonValueKind.Object)
            foreach (var p in k.EnumerateObject())
                keys[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw Bad("continuumKeys", $"'{p.Name}' must be text");

        var index = 0;
        var steps = new List<TutorialStep>();
        foreach (var s in Arr(root, "steps", "script"))
        {
            var id = Text(s, "id", "a step");
            if (steps.Any(x => x.Id == id)) throw Bad(id, "duplicate step id");
            var lines = Arr(s, "lines", id).Select(l => new TutorialLine(index++, LineText(l, id))).ToList();
            if (lines.Count == 0) throw Bad(id, "has no lines");
            var reveal = Opt(s, "reveal").Select(e => Enum(e, id, "reveal", out HudPart h) ? h : throw Bad(id, $"unknown reveal '{e}'")).ToList();
            var grant = Opt(s, "grant").Select(e => string.IsNullOrWhiteSpace(e) ? throw Bad(id, "empty grant") : e).ToList();
            steps.Add(new TutorialStep(id, Int(s, "act", id), lines, reveal, grant,
                s.TryGetProperty("engineOff", out var eo) && eo.ValueKind == JsonValueKind.True,
                Check(s, id), s.TryGetProperty("pauseSeconds", out _) ? Int(s, "pauseSeconds", id) : 0,
                Cast(s, "cast", id), Cast(s, "leanCast", id)));
        }

        var prompts = new List<TutorialLine>();
        foreach (var p in root.TryGetProperty("prompts", out var pa) && pa.ValueKind == JsonValueKind.Array ? pa.EnumerateArray() : [])
        {
            var pid = Text(p, "id", "a prompt");
            if (prompts.Any(x => x.Id == pid)) throw Bad(pid, "duplicate prompt id");
            prompts.Add(new TutorialLine(index++, LineText(p.TryGetProperty("text", out var t) ? t : default, pid), pid));
        }
        return new TutorialScript(mentor, keys, steps, prompts);
    }

    private static TutorialCheck Check(JsonElement s, string id)
    {
        if (!s.TryGetProperty("check", out var c) || c.ValueKind != JsonValueKind.Object) throw Bad(id, "missing check");
        var kind = Enum(Text(c, "kind", id), id, "check", out CheckKind ck) ? ck : throw Bad(id, $"unknown check '{Text(c, "kind", id)}'");
        string? weapon = c.TryGetProperty("weapon", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        if (kind == CheckKind.Weapon && !Weapons.Contains(weapon)) throw Bad(id, "weapon check needs weapon bullet|bomb|mine|repel");
        int N(string n) => c.TryGetProperty(n, out _) ? Int(c, n, id) : 0;
        var count = N("count");
        if (kind == CheckKind.Kills && count < 1) throw Bad(id, "kills check needs count");
        return new TutorialCheck(kind, N("x"), N("y"), N("radius"), weapon, count, N("orAfterSeconds"));
    }

    private static List<CastLine> Cast(JsonElement s, string name, string id)
    {
        var list = new List<CastLine>();
        if (!s.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in a.EnumerateArray())
        {
            var role = Text(c, "role", id);
            if (role is not ("target" or "swarm" or "teammate")) throw Bad(id, $"unknown cast role '{role}'");
            var seat = Text(c, "seat", id);
            if (seat is not ("in" or "spec")) throw Bad(id, $"unknown cast seat '{seat}'");
            list.Add(new CastLine(role, seat, Text(c, "script", id)));
        }
        return list;
    }

    private static bool Enum<T>(string name, string id, string what, out T value) where T : struct, System.Enum
    {
        value = default;
        return !int.TryParse(name, out _) && System.Enum.TryParse(name, true, out value) && System.Enum.IsDefined(value);
    }

    private static IEnumerable<string> Opt(JsonElement s, string name) =>
        s.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : "")
            : [];

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name, string where) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : throw Bad(where, $"missing '{name}' array");

    private static string Text(JsonElement e, string name, string where) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw Bad(where, $"missing {name}");

    private static int Int(JsonElement e, string name, string where) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : throw Bad(where, $"{name} must be a number");

    // Chat is single-byte text; a line is one speech bubble.
    private static string LineText(JsonElement l, string where) =>
        l.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.GetString()) && l.GetString()!.All(c => c is >= ' ' and <= '~')
            ? l.GetString()!
            : throw Bad(where, "line must be non-empty plain ASCII text");

    private static FormatException Bad(string where, string why) => new($"tutorial script: '{where}' {why}");

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
