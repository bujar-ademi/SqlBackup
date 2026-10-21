namespace SqlBackup.Core;

public sealed record StoragePasswords(string? Destination = null, string? Secondary = null, string? Source = null);
