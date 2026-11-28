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
            return (TutorialStart.Resume, record.Act ?? 0);
        }

        return enteringPublic && capacityLeft ? (TutorialStart.Start, 0) : (TutorialStart.Offer, 0);
    }
}
