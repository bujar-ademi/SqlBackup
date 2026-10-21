namespace SqlBackup.Core;

public sealed record DatabaseInfo(
    string Name,
    string State,
    string RecoveryModel,
    bool CanBackUp,
    string Availability,
    bool CanBackUpLog = false);
