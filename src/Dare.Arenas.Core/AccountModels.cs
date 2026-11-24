namespace Dare.Arenas.Core;

public enum LoginResult { Ok, UnknownName, BadPassword, LockedOut, Disabled }

public enum CodePurpose { Signup, Recovery, AddEmail }

public sealed record Account(long Id, string Name, string? Email, bool EmailVerified, DateTimeOffset Created, DateTimeOffset? LastLogin, string? IsojName, string Status = "active");
