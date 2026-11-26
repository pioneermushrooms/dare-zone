namespace Dare.Arenas.Core;

/// <summary>Wording for arena restart countdowns.</summary>
public static class RestartTimes
{
    /// <summary>"40 seconds", "4 minutes", "3 hours 5 minutes", "2 days 1 hour": the largest two units, rounded.</summary>
    public static string Until(TimeSpan left)
    {
        var s = (int)Math.Max(0, Math.Round(left.TotalSeconds));
        static string Unit(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
        if (s < 60) return Unit(s, "second");
        var m = (s + 30) / 60;
        if (m < 60) return Unit(m, "minute");
        var h = m / 60;
        if (h < 24) return m % 60 == 0 ? Unit(h, "hour") : $"{Unit(h, "hour")} {Unit(m % 60, "minute")}";
        var d = h / 24;
        return h % 24 == 0 ? Unit(d, "day") : $"{Unit(d, "day")} {Unit(h % 24, "hour")}";
    }
}
