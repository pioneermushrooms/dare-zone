using System.Globalization;

namespace Dare.Arenas.Core;

/// <summary>"bool", "int", "enum", "flags" or "text": what kind of value a setting holds.</summary>
public enum SettingKind { Text, Bool, Int, Enum, Flags }

/// <summary>
/// What values one setting accepts, from the server's config help (its type, Min and Max). Used
/// by every path that writes a setting (web page, ?arenaset, the in-game settings editor), so a
/// value the arena can't use is refused with a reason instead of written.
/// </summary>
public sealed record SettingSpec(SettingKind Kind, long? Min, long? Max, IReadOnlyList<string> Choices, long FlagMask)
{
    /// <summary>Any value (only the text rules of <see cref="SettingValue"/>).</summary>
    public static readonly SettingSpec AnyText = new(SettingKind.Text, null, null, [], 0);

    /// <summary>The numbers of an enum's choices (empty for other kinds).</summary>
    public IReadOnlyList<long> Values { get; init; } = [];

    /// <summary>
    /// The spec for a setting documented as <paramref name="type"/> with the help's Min/Max text.
    /// Integer types are also held to the range of the type (a short setting can't be 40000).
    /// </summary>
    public static SettingSpec From(Type? type, string? min, string? max)
    {
        if (type is null) return AnyText;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool)) return new(SettingKind.Bool, 0, 1, [], 0);
        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            if (type.IsDefined(typeof(FlagsAttribute), false))
            {
                long mask = 0;
                foreach (var v in Enum.GetValues(type)) mask |= Convert.ToInt64(v, CultureInfo.InvariantCulture);
                return new(SettingKind.Flags, null, null, names, mask);
            }
            var values = Enum.GetValues(type).Cast<object>().Select(v => Convert.ToInt64(v, CultureInfo.InvariantCulture)).ToArray();
            return new(SettingKind.Enum, null, null, names, 0) { Values = values };
        }

        (long lo, long hi)? range = Type.GetTypeCode(type) switch
        {
            TypeCode.Byte => (byte.MinValue, byte.MaxValue),
            TypeCode.SByte => (sbyte.MinValue, sbyte.MaxValue),
            TypeCode.Int16 => (short.MinValue, short.MaxValue),
            TypeCode.UInt16 => (ushort.MinValue, ushort.MaxValue),
            TypeCode.Int32 => (int.MinValue, int.MaxValue),
            TypeCode.UInt32 => (uint.MinValue, uint.MaxValue),
            TypeCode.Int64 => (long.MinValue, long.MaxValue),
            _ => null,
        };
        if (range is not { } r) return AnyText;
        var lo = ParseLong(min) is { } a ? Math.Max(a, r.lo) : r.lo;
        var hi = ParseLong(max) is { } b ? Math.Min(b, r.hi) : r.hi;
        return new(SettingKind.Int, lo, hi, [], 0);
    }

    /// <summary>
    /// Checks <paramref name="value"/> (already through <see cref="SettingValue"/>'s text rules)
    /// against this spec. Yes/no words of a bool become 1 or 0; numbers lose leading '+' and zeros.
    /// </summary>
    public bool TryCheck(string name, string value, out string normalized, out string error)
    {
        normalized = value;
        error = "";
        switch (Kind)
        {
            case SettingKind.Bool:
                if (ParseBool(value) is { } on) { normalized = on ? "1" : "0"; return true; }
                error = $"{name} is on or off: use 1 or 0.";
                return false;

            case SettingKind.Int:
                if (ParseLong(value) is not { } n)
                {
                    error = $"{name} must be a whole number{RangeText()}.";
                    return false;
                }
                if (n < Min || n > Max)
                {
                    error = $"{name} must be{RangeText()}; {n} is out of range.";
                    return false;
                }
                normalized = n.ToString(CultureInfo.InvariantCulture);
                return true;

            case SettingKind.Enum:
                foreach (var c in Choices)
                    if (c.Equals(value, StringComparison.OrdinalIgnoreCase)) { normalized = c; return true; }
                // the number of a choice, as older conf files write it (the server reads both)
                if (ParseLong(value) is { } num && Values.Contains(num))
                {
                    normalized = num.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                error = $"{name} must be one of: {string.Join(", ", Choices)}.";
                return false;

            case SettingKind.Flags:
                if (ParseLong(value) is { } bits && bits >= 0 && (bits & ~FlagMask) == 0)
                {
                    normalized = bits.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var picked = new List<string>();
                foreach (var p in parts)
                {
                    var c = Choices.FirstOrDefault(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
                    if (c is null) { picked.Clear(); break; }
                    picked.Add(c);
                }
                if (picked.Count > 0) { normalized = string.Join(", ", picked); return true; }
                error = $"{name} is a combination of: {string.Join(", ", Choices)} (names separated by commas, or a number).";
                return false;

            default:
                return true;
        }
    }

    /// <summary>" from 0 to 255" (blank when the setting has no range).</summary>
    public string RangeText() =>
        Kind == SettingKind.Int && Min is { } lo && Max is { } hi ? $" from {lo} to {hi}" : "";

    /// <summary>A short line for players: "0 or 1", "0 to 255", "one of: A, B", or "".</summary>
    public string Describe() => Kind switch
    {
        SettingKind.Bool => "0 (off) or 1 (on)",
        SettingKind.Int when Min is { } lo && Max is { } hi => $"{lo} to {hi}",
        SettingKind.Enum => $"one of: {string.Join(", ", Choices)}",
        SettingKind.Flags => $"any of: {string.Join(", ", Choices)}",
        _ => "",
    };

    private static long? ParseLong(string? s) =>
        long.TryParse(s?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool? ParseBool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "1" or "y" or "yes" or "true" or "on" => true,
        "0" or "n" or "no" or "false" or "off" => false,
        _ => null,
    };
}
