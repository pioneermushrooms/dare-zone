using System.Security.Cryptography;
using System.Text;

namespace Dare.Arenas.Core;

/// <summary>
/// Game passes: the browser's Play button signs a pilot in to the zone without the password. A
/// signed-in web session asks for a pass (<see cref="IssueGamePass"/>); the game sends it as the
/// password, and the zone's login takes it (<see cref="RedeemGamePass"/>) once, within
/// <see cref="GamePassLifetime"/>, for that account only. Only a hash is stored. A string that is
/// not a valid pass falls through to the normal password check, so a password that happens to
/// look like a pass still works.
/// </summary>
public sealed partial class AccountStore
{
    public static readonly TimeSpan GamePassLifetime = TimeSpan.FromSeconds(60);
    public const int MaxOpenGamePasses = 3;
    public const string GamePassPrefix = "gp-";
    private const int GamePassRandomChars = 24; // 24 of 36 characters, about 124 bits
    private const string GamePassAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>The shape of a pass: the prefix and 24 lowercase letters or digits (27 characters, under the game's 31).</summary>
    public static bool LooksLikeGamePass(string? s) =>
        s is { Length: 27 } && s.StartsWith(GamePassPrefix, StringComparison.Ordinal)
        && s.AsSpan(GamePassPrefix.Length).IndexOfAnyExcept(GamePassAlphabet) < 0;

    /// <summary>
    /// A new single-use pass for <paramref name="accountId"/> (an active account), or null when the
    /// account can't sign in. Expired passes are cleared; at most <see cref="MaxOpenGamePasses"/>
    /// stay open per account (the oldest goes).
    /// </summary>
    public string? IssueGamePass(long accountId)
    {
        var pass = GamePassPrefix + RandomNumberGenerator.GetString(GamePassAlphabet, GamePassRandomChars);
        string? issued = null;
        Tx(() =>
        {
            var now = _clock();
            Run("DELETE FROM game_passes WHERE expires <= $now", ("$now", Fmt(now)));
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id AND status = 'active'", ("$id", accountId)) == 0)
                return Fail("This account can't sign in.");
            Run("""
                DELETE FROM game_passes WHERE account_id = $id AND pass_hash NOT IN
                  (SELECT pass_hash FROM game_passes WHERE account_id = $id ORDER BY expires DESC LIMIT $keep)
                """, ("$id", accountId), ("$keep", MaxOpenGamePasses - 1));
            Run("INSERT INTO game_passes(pass_hash, account_id, expires) VALUES($h, $id, $exp)",
                ("$h", GamePassHash(pass)), ("$id", accountId), ("$exp", Fmt(now + GamePassLifetime)));
            issued = pass;
            return Ok("");
        });
        return issued;
    }

    /// <summary>
    /// Takes <paramref name="pass"/> for the account named <paramref name="name"/>: true (with the
    /// account, and its last login recorded) when it is an open pass of that active account; the
    /// pass is used up either way it matched. False when it isn't one: the caller then checks it as
    /// a password.
    /// </summary>
    public bool RedeemGamePass(string name, string pass, out Account? account)
    {
        account = null;
        if (!LooksLikeGamePass(pass)) return false;
        long? id = null;
        Tx(() =>
        {
            var now = _clock();
            using (var c = Cmd("""
                SELECT a.id FROM game_passes g JOIN accounts a ON a.id = g.account_id
                WHERE g.pass_hash = $h AND g.expires > $now AND a.name = $n AND a.status = 'active'
                """, ("$h", GamePassHash(pass)), ("$now", Fmt(now)), ("$n", name ?? "")))
            {
                if (c.ExecuteScalar() is long found) id = found;
            }
            if (id is null) return Fail("");
            Run("DELETE FROM game_passes WHERE pass_hash = $h", ("$h", GamePassHash(pass)));
            Run("UPDATE accounts SET last_login = $t WHERE id = $id", ("$t", Fmt(now)), ("$id", id.Value));
            Run("DELETE FROM login_failures WHERE name = $n", ("$n", name ?? ""));
            return Ok("");
        });
        if (id is null) return false;
        account = Get(id.Value);
        return account is not null;
    }

    private static string GamePassHash(string pass) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("game-pass:" + pass))).ToLowerInvariant();
}
