namespace Dare.Arenas.Core;

public enum ArenaStatus { Active, Suspended, Archived }
public enum RequestStatus { Pending, Approved, Declined, Withdrawn }

public sealed record ArenaRequest(long Id, string Arena, string Player, string Pitch, RequestStatus Status, string? DecidedBy, string? Reason, DateTimeOffset At);
public sealed record OwnedArena(string Name, string Owner, ArenaStatus Status, DateTimeOffset Created, DateTimeOffset LastEntered, IReadOnlyList<string> CoMods);
public sealed record ArenaEvent(long Id, DateTimeOffset At, string Arena, string Actor, string Kind, string Detail);
public sealed record Result(bool Ok, string Message);

/// <summary>A draft's place on the way to a public arena (see ArenaStore drafts).</summary>
public enum DraftStatus { Active, Requested, Published, Archived }

/// <summary>A pilot's draft <c>#Name</c>. <see cref="Live"/>: approved, so <c>Name</c> exists as a public arena.</summary>
public sealed record DraftInfo(string Name, string Owner, DraftStatus Status, bool Live, DateTimeOffset Created,
    DateTimeOffset LastTouched, IReadOnlyList<string> CoMods, IReadOnlyList<string> Invited);
