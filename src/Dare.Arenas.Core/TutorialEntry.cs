namespace Dare.Arenas.Core;

/// <summary>What is entering: a signed-in pilot, a guest, a bot, or a fake (scripted) player.</summary>
public enum EntryKind { Account, Guest, Bot, Fake }

/// <summary>What the zone does with an entry: nothing, start the tutorial, resume it, or offer it.</summary>
public enum TutorialStart { No, Start, Resume, Offer }

/// <summary>The rules for who is new and what to do about it (pure; the store only supplies the record).</summary>
public static class TutorialEntry
{
    /// <summary>Drop-outs after which an unfinished tutorial is no longer resumed.</summary>
    public const int MaxDropouts = 2;

    /// <param name="record">The person's tutorial record; null when there is none (a new account, or a guest).</param>
    /// <param name="guestSaysDone">A guest's own client says the tutorial was done.</param>
    /// <param name="enteringPublic">The entry is into the public arena (not a named or private one).</param>
    /// <param name="firstEntryThisLogin">Only the first arena entry of a login counts.</param>
    /// <param name="capacityLeft">There is room for another tutorial run.</param>
    /// <returns>What to do, and the act to resume at (0 unless <see cref="TutorialStart.Resume"/>).</returns>
    public static (TutorialStart What, int Act) Decide(EntryKind kind, TutorialRecord? record, bool guestSaysDone,
        bool enteringPublic, bool firstEntryThisLogin, bool capacityLeft)
    {
        if (kind is EntryKind.Bot or EntryKind.Fake) return (TutorialStart.No, 0);
        if (kind == EntryKind.Guest && guestSaysDone) return (TutorialStart.No, 0);
        if (!firstEntryThisLogin) return (TutorialStart.No, 0);

        if (kind == EntryKind.Account && record is not null)
        {
            if (record.Outcome is not null || record.Dropouts >= MaxDropouts) return (TutorialStart.No, 0);
            // no step yet: the row is from a login that only offered the tutorial (another arena first), and the
            // account chose not to play it; only a run under way (it has a step) is picked up again
            if (record.Step is null) return (TutorialStart.No, 0);
            return enteringPublic && capacityLeft ? (TutorialStart.Resume, record.Act ?? 0) : (TutorialStart.Offer, 0);
        }

        return enteringPublic && capacityLeft ? (TutorialStart.Start, 0) : (TutorialStart.Offer, 0);
    }

    /// <summary>
    /// Whether an arena entry is a game login the tutorial notes (the account is then never new again): an account's
    /// first entry of a login, from a game client. A chat-protocol client is not one, nor are the web pages (they never
    /// enter an arena).
    /// </summary>
    public static bool IsGameLogin(EntryKind? kind, bool standardClient, bool firstEntryThisLogin) =>
        kind == EntryKind.Account && standardClient && firstEntryThisLogin;

    /// <summary>
    /// Whether someone arriving in a training arena may stay: the player whose run it is (or who is on the way to it),
    /// the run's bots and fake players, and staff looking in. Anyone else is sent back to the public arena.
    /// </summary>
    public static bool MayStayInTraining(EntryKind? kind, bool ownRun, bool staff) =>
        ownRun || staff || kind is EntryKind.Bot or EntryKind.Fake;

    /// <summary>The act a <c>?tutorial</c> name starts at: controls 1, swarm 2, objectives 3; null for anything else.</summary>
    public static int? ActNamed(string name) => name.Trim().ToLowerInvariant() switch
    {
        "controls" => 1,
        "swarm" => 2,
        "objectives" => 3,
        _ => null,
    };
}
