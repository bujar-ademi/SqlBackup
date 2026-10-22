namespace SqlBackup.Core;

public sealed record DestinationPassword(Guid DestinationId, string? Secret, string? SessionToken = null);
