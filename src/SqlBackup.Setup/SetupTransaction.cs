namespace SqlBackup.Setup;

public sealed record SetupTransaction(string InstallDirectory, string Version, bool HadService,
    bool WasRunning, bool HadDatabase, string State = "Prepared");
