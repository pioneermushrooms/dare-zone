namespace Dare.Arenas.Core;

/// <summary>One <c>Section:Key = value</c> read from a settings file (line is 1-based).</summary>
public sealed record SettingsFileEntry(string Section, string Key, string Value, int Line);

/// <summary>A line of a settings file that was not read as a setting, and why.</summary>
public sealed record SettingsFileSkip(int Line, string Text, string Reason);

/// <summary>
/// Reads a settings file a pilot drops on the arena editor: an arena.conf, a .cfg/.ini, or a
/// Continuum-style .set. All are INI text: <c>[Section]</c> headers and <c>Key=Value</c> lines.
/// Also read: <c>Section:Key=Value</c> and the server's <c>Section:Key:Value</c> lines. Comments
/// start with ';', '/' or '#'; '#' directives (#include, #define, ...) are listed as skipped, since an
/// uploaded file can't pull in other files. Ship settings under <c>[All]</c> are expanded to every
/// ship, as the server documents them. A key given twice keeps its first spelling and its last value. Values are only
/// trimmed here; the allow-list and value checks happen when the settings are applied.
/// </summary>
public static class SettingsFile
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxEntries = 5000;

    public static readonly string[] Ships = ["Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark"];

    public static (IReadOnlyList<SettingsFileEntry> Entries, IReadOnlyList<SettingsFileSkip> Skipped) Parse(string text)
    {
        var byKey = new Dictionary<(string, string), SettingsFileEntry>();
        var order = new List<(string, string)>();
        var skipped = new List<SettingsFileSkip>();
        string? section = null;
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        void Add(string s, string k, string v, int line)
        {
            foreach (var sec in string.Equals(s, "All", StringComparison.OrdinalIgnoreCase) ? Ships : [s])
            {
                var id = (sec.ToLowerInvariant(), k.ToLowerInvariant());
                if (byKey.TryGetValue(id, out var had)) { byKey[id] = had with { Value = v, Line = line }; continue; }
                if (byKey.Count >= MaxEntries) { skipped.Add(new(line, Clip($"{s}:{k}"), $"more than {MaxEntries} settings")); return; }
                order.Add(id);
                byKey[id] = new(sec, k, v, line);
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var n = i + 1;
            var t = lines[i].Trim();
            if (i == 0) t = t.TrimStart('﻿');
            if (t.Length == 0 || t[0] is ';' or '/') continue;
            if (t[0] == '#')
            {
                if (t.Length > 1 && char.IsAsciiLetter(t[1])) skipped.Add(new(n, Clip(t), "directives such as #include aren't read from an uploaded file"));
                continue;
            }
            if (t[0] == '[')
            {
                var close = t.IndexOf(']');
                var name = close > 0 ? t[1..close].Trim() : "";
                if (IsWord(name)) section = name;
                else { section = null; skipped.Add(new(n, Clip(t), "not a section name")); }
                continue;
            }

            // Section:Key=Value, Section:Key:Value (the server's own form), or Key=Value under a section
            var eq = t.IndexOf('=');
            var head = eq >= 0 ? t[..eq].Trim() : t;
            var value = eq >= 0 ? t[(eq + 1)..].Trim() : null;
            var colon = head.IndexOf(':');
            if (colon > 0)
            {
                var s = head[..colon].Trim();
                var rest = head[(colon + 1)..];
                var k = rest;
                if (value is null)
                {
                    var c2 = rest.IndexOf(':');
                    if (c2 < 0) { skipped.Add(new(n, Clip(t), "no value (use Key=Value)")); continue; }
                    k = rest[..c2];
                    value = rest[(c2 + 1)..].Trim();
                }
                k = k.Trim();
                if (!IsWord(s) || !IsWord(k)) { skipped.Add(new(n, Clip(t), "not a Section:Key name")); continue; }
                Add(s, k, value, n);
                continue;
            }
            if (value is null) { skipped.Add(new(n, Clip(t), "no value (use Key=Value)")); continue; }
            if (!IsWord(head)) { skipped.Add(new(n, Clip(t), "not a setting name")); continue; }
            if (section is null) { skipped.Add(new(n, Clip(t), "no [Section] above it")); continue; }
            Add(section, head, value, n);
        }

        return (order.Select(id => byKey[id]).ToList(), skipped);
    }

    private static string Clip(string s) => s.Length <= 80 ? s : s[..77] + "...";

    internal static bool IsWord(string s)
    {
        if (s.Length == 0 || s.Length > 64) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        return true;
    }
}
