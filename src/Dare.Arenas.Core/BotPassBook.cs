namespace Dare.Arenas.Core;

/// <summary>
/// One bot host's one-time login passes: which pass each listed bot carries in the lineup, and when it gets a new one.
/// A pass works once (the zone's login takes it), so the host must always hold an unspent pass for every bot it may
/// (re)connect: after a bot logs in its next pass is issued at once, because a host restart (any script, ship or team
/// edit) logs the bot in again straight away. The host keeps a new pass for a connected bot without reconnecting it.
/// </summary>
/// <param name="issue">Issues a fresh pass for the name (replacing any pending one) and returns it.</param>
/// <param name="unspent">True while that pass is still waiting to be used.</param>
/// <param name="revoke">Withdraws the name's pending pass.</param>
public sealed class BotPassBook(Func<string, string> issue, Func<string, string, bool> unspent, Action<string> revoke)
{
    /// <summary>A listed bot that has not shown up in the zone this long gets a fresh pass.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, (string Password, DateTimeOffset Issued)> _passes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the bot carries a pass from this book (it is this host's bot).</summary>
    public bool Holds(string label) => _passes.ContainsKey(label);

    /// <summary>
    /// The pass to list the bot with, or null when it should not be listed: a bot in the zone that this book never
    /// gave a pass is an old session, left alone until it has gone.
    /// </summary>
    public string? PassFor(string label, bool inZone, DateTimeOffset now)
    {
        if (_passes.TryGetValue(label, out var pass))
        {
            bool spent = !unspent(label, pass.Password);
            bool stale = !inZone && now - pass.Issued >= Lifetime; // never came in on it: the host reconnects on a new one
            if (spent || stale) _passes[label] = pass = (issue(label), now);
            return pass.Password;
        }
        if (inZone) return null;
        _passes[label] = pass = (issue(label), now);
        return pass.Password;
    }

    /// <summary>Withdraws the passes of bots no longer listed.</summary>
    public void KeepOnly(IReadOnlyCollection<string> listed)
    {
        var keep = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        foreach (var label in _passes.Keys.Where(l => !keep.Contains(l)).ToList())
        {
            revoke(label);
            _passes.Remove(label);
        }
    }

    /// <summary>Withdraws every pass (the host stopped or restarted).</summary>
    public void Clear() => KeepOnly([]);
}
