namespace SqlBackup.Setup;

public sealed record InstallationRecord(string Version, string InstallDirectory, string ServiceIdentity = "S-1-5-18");
