namespace Dare.Arenas.Core;

/// <summary>What the owner of one pilot name chose for its public profile, plus staff's hide flag.</summary>
public sealed record PilotProfileSettings(string Bio, long? AvatarShip, bool ShowContribs, bool ShowArenas, bool ShowShips, bool ModHidden);

/// <summary>
/// The public face of one pilot name (<c>/pilot/&lt;name&gt;</c>). Deliberately nothing else: no
/// email, sign-in times, account id, or the other names on the same email.
/// </summary>
public sealed record PublicPilot(
    string Name, DateTimeOffset Since, string Bio, long? Avatar,
    bool ShowContribs, bool ShowArenas, bool ShowShips,
    IReadOnlyList<ShipyardShip> Ships, bool Hidden);

/// <summary>
/// Public pilot profiles: one row per pilot name (none until the owner saves), kept apart from the
/// account itself. Staff can hide a profile (it then reads as unknown to everyone else) or clear its
/// bio; the owner's own saves never undo a hide.
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxBioLength = 280;

    private static readonly PilotProfileSettings DefaultProfile = new("", null, true, true, true, false);

    private void CreateProfileTable() => Exec("""
        CREATE TABLE IF NOT EXISTS pilot_profiles (
          account_id INTEGER NOT NULL PRIMARY KEY,
          bio TEXT NOT NULL DEFAULT '',
          avatar_ship INTEGER NULL,
          show_contribs INTEGER NOT NULL DEFAULT 1,
          show_arenas INTEGER NOT NULL DEFAULT 1,
          show_ships INTEGER NOT NULL DEFAULT 1,
          mod_hidden INTEGER NOT NULL DEFAULT 0,
          updated TEXT NOT NULL
        );
        """);

    /// <summary>The profile settings of <paramref name="accountId"/> (the defaults when it has none).</summary>
    public PilotProfileSettings ProfileSettings(long accountId)
    {
        lock (_gate) return LoadProfile(accountId);
    }

    /// <summary>The owner's save. The avatar must be one of the name's own Shipyard ships (or null).</summary>
    public Result SaveProfile(long accountId, string? bio, long? avatarShip, bool showContribs, bool showArenas, bool showShips)
    {
        var clean = CleanBio(bio);
        if (clean.Length > MaxBioLength) return Fail($"Keep the bio to {MaxBioLength} characters.");
        return Tx(() =>
        {
            var me = LoadAccount("id = $id", ("$id", accountId));
            if (me is null || me.Status != "active") return Fail("Sign in again first.");
            if (avatarShip is { } ship &&
                Scalar("SELECT COUNT(*) FROM shipyard_ships WHERE id = $s AND person_id = $a", ("$s", ship), ("$a", PersonKey(accountId))) == 0)
                return Fail("Pick one of your own Shipyard ships.");
            Run("""
                INSERT INTO pilot_profiles(account_id, bio, avatar_ship, show_contribs, show_arenas, show_ships, mod_hidden, updated)
                VALUES($a, $b, $v, $c, $r, $s, 0, $u)
                ON CONFLICT(account_id) DO UPDATE SET bio = $b, avatar_ship = $v, show_contribs = $c,
                  show_arenas = $r, show_ships = $s, updated = $u
                """,
                ("$a", accountId), ("$b", clean), ("$v", avatarShip is { } v ? v : DBNull.Value),
                ("$c", showContribs ? 1 : 0), ("$r", showArenas ? 1 : 0), ("$s", showShips ? 1 : 0), ("$u", Now()));
            return Ok("Profile saved.");
        });
    }

    /// <summary>Staff: clear the bio of <paramref name="name"/> and/or hide or unhide its profile.</summary>
    public Result ModerateProfile(string name, bool? clearBio, bool? hidden) => Tx(() =>
    {
        var a = LoadAccount("name = $n", ("$n", (name ?? "").Trim()));
        if (a is null) return Fail($"No pilot named '{name}'.");
        Run("INSERT INTO pilot_profiles(account_id, updated) VALUES($a, $u) ON CONFLICT(account_id) DO NOTHING", ("$a", a.Id), ("$u", Now()));
        if (clearBio == true) Run("UPDATE pilot_profiles SET bio = '' WHERE account_id = $a", ("$a", a.Id));
        if (hidden is { } h) Run("UPDATE pilot_profiles SET mod_hidden = $h WHERE account_id = $a", ("$a", a.Id), ("$h", h ? 1 : 0));
        var what = new List<string>();
        if (clearBio == true) what.Add("bio cleared");
        if (hidden is { } h2) what.Add(h2 ? "profile hidden" : "profile shown");
        return Ok(what.Count == 0 ? "Nothing changed." : $"{a.Name}: {string.Join(", ", what)}.");
    });

    /// <summary>
    /// The public view of <paramref name="name"/> (case-insensitive), or null for an unknown or
    /// inactive name, or one staff hid unless <paramref name="privileged"/> (its owner or staff).
    /// </summary>
    public PublicPilot? PublicPilot(string name, bool privileged)
    {
        lock (_gate)
        {
            var a = LoadAccount("name = $n", ("$n", (name ?? "").Trim()));
            if (a is null || a.Status != "active") return null;
            var p = LoadProfile(a.Id);
            if (p.ModHidden && !privileged) return null;
            var (ships, slots) = Shipyard(a.Id);
            var avatar = p.AvatarShip is { } av && ships.Any(s => s.Id == av) ? av : (long?)null;
            var shown = p.ShowShips
                ? slots.Where(id => id != 0).Distinct().Select(id => ships.FirstOrDefault(s => s.Id == id)).OfType<ShipyardShip>().ToList()
                : new List<ShipyardShip>();
            return new PublicPilot(a.Name, a.Created, p.Bio, avatar, p.ShowContribs, p.ShowArenas, p.ShowShips, shown, p.ModHidden);
        }
    }

    /// <summary>A ship sheet a profile shows: its avatar, or (when ships are shown) a slotted ship. Null otherwise or when hidden.</summary>
    public byte[]? ProfileShipSheet(string name, long shipId)
    {
        lock (_gate)
        {
            var pub = PublicPilot(name, privileged: false);
            if (pub is null || (pub.Avatar != shipId && !pub.Ships.Any(s => s.Id == shipId))) return null;
            var a = LoadAccount("name = $n", ("$n", (name ?? "").Trim()))!;
            return ShipyardSheet(a.Id, shipId);
        }
    }

    /// <summary>Under the lock.</summary>
    private PilotProfileSettings LoadProfile(long accountId)
    {
        using var c = Cmd("SELECT bio, avatar_ship, show_contribs, show_arenas, show_ships, mod_hidden FROM pilot_profiles WHERE account_id = $a", ("$a", accountId));
        using var r = c.ExecuteReader();
        if (!r.Read()) return DefaultProfile;
        return new PilotProfileSettings(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1),
            r.GetInt64(2) != 0, r.GetInt64(3) != 0, r.GetInt64(4) != 0, r.GetInt64(5) != 0);
    }

    /// <summary>Plain text: control characters (other than line breaks) dropped, CRLF to LF, trimmed.</summary>
    public static string CleanBio(string? bio)
    {
        var s = (bio ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        return new string(s.Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray()).Trim();
    }
}
