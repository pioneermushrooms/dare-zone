namespace Dare.Arenas.Core;

public enum ArenaStatus { Active, Suspended, Archived }
public enum RequestStatus { Pending, Approved, Declined }

public sealed record ArenaRequest(long Id, string Arena, string Player, string Pitch, RequestStatus Status, string? DecidedBy, string? Reason, DateTimeOffset At);
public sealed record OwnedArena(string Name, string Owner, ArenaStatus Status, DateTimeOffset Created, DateTimeOffset LastEntered, IReadOnlyList<string> CoMods);
public sealed record ArenaEvent(long Id, DateTimeOffset At, string Arena, string Actor, string Kind, string Detail);
public sealed record Result(bool Ok, string Message);
