namespace SqlBackup.Infrastructure;

public sealed record ShareCredentialSnapshot(string Folder, string UserName, byte[] ProtectedPassword);
