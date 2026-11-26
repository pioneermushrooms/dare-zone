using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dare.Arenas.Core;

public enum VoiceScope { Global, Team }

/// <summary>One voice-menu line: <see cref="Code"/> is the letters typed after V (e.g. "gw").</summary>
public sealed record VoicePhrase(string Code, byte Sound, VoiceScope Scope, string Label, string Text, string Delivery);

/// <summary>A voice-menu level (e.g. "g" Global, "gc" Compliment).</summary>
public sealed record VoiceMenu(string Code, string Label);

/// <summary>
/// The voice-menu phrase table (<c>voice/phrases.json</c>): the single source the plugin, the clip
/// tooling and the client's generated table all read. Sound ids are 128–255, one per phrase.
/// </summary>
public sealed partial class VoicePhrases
{
    private static readonly HashSet<string> Deliveries = ["calm", "urgent", "hype", "smug"];
    private readonly Dictionary<string, VoicePhrase> _byCode;

    public IReadOnlyList<VoiceMenu> Menus { get; }
    public IReadOnlyList<VoicePhrase> Phrases { get; }

    private VoicePhrases(List<VoiceMenu> menus, List<VoicePhrase> phrases)
    {
        Menus = menus;
        Phrases = phrases;
        _byCode = phrases.ToDictionary(p => p.Code);
    }

    /// <summary>Looks up a typed code, ignoring case and surrounding spaces. Menus aren't phrases.</summary>
    public bool TryGet(string typed, out VoicePhrase phrase) =>
        _byCode.TryGetValue(typed.Trim().ToLowerInvariant(), out phrase!);

    /// <summary>Parses and validates a table; throws <see cref="FormatException"/> naming what's wrong.</summary>
    public static VoicePhrases Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new FormatException($"voice table: {ex.Message}", ex); }

        var menus = new List<VoiceMenu>();
        foreach (var m in Array(root, "menus"))
        {
            var code = Code(m);
            if (menus.Any(x => x.Code == code)) throw Bad(code, "duplicate menu");
            menus.Add(new VoiceMenu(code, Str(m, "label", code)));
        }
        var menuCodes = menus.Select(m => m.Code).ToHashSet();
        foreach (var m in menus.Where(m => m.Code.Length > 1 && !menuCodes.Contains(m.Code[..^1])))
            throw Bad(m.Code, "menu has no parent menu");

        var phrases = new List<VoicePhrase>();
        foreach (var p in Array(root, "phrases"))
        {
            var code = Code(p);
            if (menuCodes.Contains(code)) throw Bad(code, "is both a menu and a phrase");
            if (code.Length < 2 || !menuCodes.Contains(code[..^1])) throw Bad(code, "phrase has no menu");
            if (phrases.Any(x => x.Code == code)) throw Bad(code, "duplicate phrase");
            if (!p.TryGetProperty("sound", out var s) || !s.TryGetInt32(out var sound) || sound is < 128 or > 255)
                throw Bad(code, "sound must be 128-255");
            if (phrases.Any(x => x.Sound == sound)) throw Bad(code, $"sound {sound} already used");
            var scope = Str(p, "scope", code) switch
            {
                "global" => VoiceScope.Global,
                "team" => VoiceScope.Team,
                _ => throw Bad(code, "scope must be global or team"),
            };
            var delivery = Str(p, "delivery", code);
            if (!Deliveries.Contains(delivery)) throw Bad(code, $"unknown delivery '{delivery}'");
            phrases.Add(new VoicePhrase(code, (byte)sound, scope, Str(p, "label", code), Str(p, "text", code), delivery));
        }
        return new VoicePhrases(menus, phrases);
    }

    private static JsonElement.ArrayEnumerator Array(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : throw new FormatException($"voice table: missing '{name}' array");

    private static string Code(JsonElement e)
    {
        var code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        return CodePattern().IsMatch(code) ? code : throw Bad(code, "code must be lower-case letters");
    }

    private static string Str(JsonElement e, string name, string code) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw Bad(code, $"missing {name}");

    private static FormatException Bad(string code, string why) => new($"voice table: '{code}' {why}");

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex CodePattern();
}
