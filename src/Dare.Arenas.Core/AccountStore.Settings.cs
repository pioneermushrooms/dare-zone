using System.Text;

namespace Dare.Arenas.Core;

/// <summary>
/// Each pilot name's own keys and macros, kept as the <c>[keys]</c> and <c>[macros]</c> sections
/// of the game's settings file. The site edits them; the game gets them when the pilot enters.
/// The game checks each line itself and skips what it can't read, so this only bounds the text.
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxSettingsBytes = 16 * 1024;
    public const int MaxSettingsLines = 400;

    /// <summary>The saved keys and macros of <paramref name="accountId"/> ("" for the defaults).</summary>
    public string PilotSettings(long accountId)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT text FROM pilot_settings WHERE account_id = $id", ("$id", accountId));
            return c.ExecuteScalar() as string ?? "";
        }
    }

    /// <summary>
    /// Saves <paramref name="text"/> for <paramref name="accountId"/>: only the lines of its
    /// <c>[keys]</c> and <c>[macros]</c> sections are kept. Empty text goes back to the defaults.
    /// </summary>
    public Result SavePilotSettings(long accountId, string text)
    {
        if (!TryCleanSettings(text, out var clean, out var error)) return Fail(error);
        return Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id AND status = 'active'", ("$id", accountId)) == 0)
                return Fail("Sign in again first.");
            if (clean.Length == 0)
                Run("DELETE FROM pilot_settings WHERE account_id = $id", ("$id", accountId));
            else
                Run("INSERT OR REPLACE INTO pilot_settings(account_id, text, updated) VALUES($id, $t, $u)",
                    ("$id", accountId), ("$t", clean), ("$u", Now()));
            return Ok(clean.Length == 0 ? "Back to the default keys and macros." : "Saved. They apply the next time you enter.");
        });
    }

    /// <summary>Keeps the <c>[keys]</c> and <c>[macros]</c> sections of <paramref name="text"/>, bounded.</summary>
    public static bool TryCleanSettings(string? text, out string clean, out string error)
    {
        clean = "";
        error = "";
        text ??= "";
        if (Encoding.UTF8.GetByteCount(text) > MaxSettingsBytes) { error = "That's too long."; return false; }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > MaxSettingsLines) { error = "That has too many lines."; return false; }
        var kept = new StringBuilder();
        var section = "";
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Any(ch => char.IsControl(ch) && ch != '\t')) { error = "Use plain text, one setting per line."; return false; }
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                section = t[1..^1].Trim().ToLowerInvariant();
                if (section is "keys" or "macros") kept.Append('[').Append(section).Append("]\n");
                continue;
            }
            if (section is not ("keys" or "macros") || t.Length == 0) continue;
            kept.Append(line).Append('\n');
        }
        clean = kept.ToString();
        return true;
    }
}
